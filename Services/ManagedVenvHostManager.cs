using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public enum ManagedHostState
{
    NotInstalled,
    Installing,
    Starting,
    Ready,
    Failed,
}

public sealed record ManagedGpuRuntimeValidationResult(
    bool CudaAvailable,
    string Message,
    string? CudaVersion = null);

public sealed class ManagedVenvHostManager : IContainerizedInferenceManager, IDisposable
{
    private static readonly TimeSpan PreflightHealthTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PostStartProbeTimeout = TimeSpan.FromSeconds(20);
    private const string PythonVersion = "3.11.6";

    private readonly AppLog _log;
    private readonly ContainerizedServiceProbe? _probe;
    private readonly Func<string, TimeSpan, CancellationToken, Task<ContainerHealthStatus>> _healthCheckFunc;
    private readonly Func<HardwareSnapshot> _hardwareSnapshotProvider;
    private readonly Func<string?> _uvResolver;
    private readonly Func<string> _runtimeRootResolver;
    private readonly Func<string> _inferenceScriptResolver;
    private readonly Func<string> _requirementsPathResolver;
    private readonly Func<string> _constraintsPathResolver;
    private readonly Func<string, CancellationToken, Task<ManagedGpuRuntimeValidationResult>> _runtimeValidator;
    private readonly object _gate = new();
    private Task<ContainerizedStartResult>? _inFlightStartTask;
    private Process? _hostProcess;

    public ManagedVenvHostManager(
        AppLog log,
        ContainerizedServiceProbe? probe = null,
        Func<string, TimeSpan, CancellationToken, Task<ContainerHealthStatus>>? healthCheckFunc = null,
        Func<HardwareSnapshot>? hardwareSnapshotProvider = null,
        Func<string?>? uvResolver = null,
        Func<string>? runtimeRootResolver = null,
        Func<string>? inferenceScriptResolver = null,
        Func<string>? requirementsPathResolver = null,
        Func<string>? constraintsPathResolver = null,
        Func<string, CancellationToken, Task<ManagedGpuRuntimeValidationResult>>? runtimeValidator = null)
    {
        _log = log;
        _probe = probe;
        _healthCheckFunc = healthCheckFunc ?? ContainerizedInferenceClient.CheckHealthAsync;
        _hardwareSnapshotProvider = hardwareSnapshotProvider ?? HardwareSnapshot.Run;
        _uvResolver = uvResolver ?? DependencyLocator.FindUv;
        _runtimeRootResolver = runtimeRootResolver ?? ManagedRuntimeLayout.GetRuntimeRoot;
        _inferenceScriptResolver = inferenceScriptResolver ?? ResolveInferenceScriptPath;
        _requirementsPathResolver = requirementsPathResolver ?? ResolveRequirementsPath;
        _constraintsPathResolver = constraintsPathResolver ?? ResolveConstraintsPath;
        _runtimeValidator = runtimeValidator ?? ValidateManagedGpuRuntimeAsync;
    }

    public ManagedHostState State { get; private set; } = ManagedHostState.NotInstalled;

    public string? FailureReason { get; private set; }

    public void RequestEnsureStarted(AppSettings settings, ContainerizedStartupTrigger trigger)
    {
        _ = EnsureStartedAsync(settings, trigger);
    }

    public async Task<ContainerizedStartResult> EnsureStartedAsync(
        AppSettings settings,
        ContainerizedStartupTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!ShouldAttemptStart(settings, trigger))
            return Skip($"Managed GPU host autostart skipped for trigger {trigger}: no GPU profile requested.");

        if (settings.PreferredLocalGpuBackend != GpuHostBackend.ManagedVenv)
            return Skip("Managed GPU host skipped because Docker backend is selected.");

        var serviceUrl = AppSettings.ManagedGpuServiceUrl;
        var preflight = await SafeCheckHealthAsync(serviceUrl, PreflightHealthTimeout, cancellationToken);
        if (preflight.IsAvailable)
        {
            State = ManagedHostState.Ready;
            return new ContainerizedStartResult(false, true, $"Managed local GPU host already available at {serviceUrl}.");
        }

        Task<ContainerizedStartResult> task;
        lock (_gate)
        {
            if (_inFlightStartTask is not null)
            {
                _log.Info($"Managed GPU host reusing in-flight start task for {serviceUrl} (trigger={trigger}).");
                task = _inFlightStartTask;
            }
            else
            {
                _inFlightStartTask = EnsureStartedCoreAsync(trigger, cancellationToken);
                task = _inFlightStartTask;
            }
        }

        try
        {
            return await task;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlightStartTask, task))
                    _inFlightStartTask = null;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (_hostProcess is { HasExited: false })
                _hostProcess.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private async Task<ContainerizedStartResult> EnsureStartedCoreAsync(
        ContainerizedStartupTrigger trigger,
        CancellationToken cancellationToken)
    {
        var hardware = _hardwareSnapshotProvider();
        if (!hardware.HasCuda)
            return Fail("Managed local GPU requires a CUDA-capable NVIDIA GPU.");

        var computeType = ManagedHostComputeTypePolicy.ResolveLaunchComputeType(hardware, ComputeProfile.Gpu);
        var uvPath = _uvResolver();
        if (string.IsNullOrWhiteSpace(uvPath))
            return Fail("uv.exe was not found. Bundle tools\\win-x64\\uv.exe or install uv on PATH.");

        var inferenceScriptPath = _inferenceScriptResolver();
        var requirementsPath = _requirementsPathResolver();
        var constraintsPath = _constraintsPathResolver();
        if (!File.Exists(inferenceScriptPath))
            return Fail($"Managed host script not found: {inferenceScriptPath}");
        if (!File.Exists(requirementsPath))
            return Fail($"Managed GPU requirements file not found: {requirementsPath}");
        if (!File.Exists(constraintsPath))
            return Fail($"Managed GPU constraints file not found: {constraintsPath}");

        var runtimeRoot = _runtimeRootResolver();
        var venvDir = Path.Combine(runtimeRoot, ".venv");
        var pythonPath = Path.Combine(venvDir, "Scripts", "python.exe");
        Directory.CreateDirectory(runtimeRoot);

        var bootstrapVersion = ComputeBootstrapVersion(inferenceScriptPath, requirementsPath, constraintsPath);
        var markerPath = Path.Combine(runtimeRoot, ".bootstrap-version");
        var markerValue = File.Exists(markerPath) ? await File.ReadAllTextAsync(markerPath, cancellationToken) : null;
        var needsBootstrap = !File.Exists(pythonPath) || !string.Equals(markerValue, bootstrapVersion, StringComparison.Ordinal);

        if (needsBootstrap)
        {
            State = ManagedHostState.Installing;
            await RunUvBootstrapAsync(uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, cancellationToken);
            await File.WriteAllTextAsync(markerPath, bootstrapVersion, cancellationToken);
        }

        var runtimeValidation = await _runtimeValidator(pythonPath, cancellationToken);
        if (!runtimeValidation.CudaAvailable)
        {
            return Fail(
                $"Managed local GPU runtime validation failed: {runtimeValidation.Message}");
        }

        State = ManagedHostState.Starting;
        await StartHostProcessAsync(
            pythonPath,
            inferenceScriptPath,
            computeType,
            Path.Combine(runtimeRoot, "managed-host.pid"),
            cancellationToken);

        var readiness = _probe is not null
            ? await _probe.WaitForProbeAsync(
                AppSettings.ManagedGpuServiceUrl,
                forceRefresh: true,
                waitTimeout: PostStartProbeTimeout,
                cancellationToken)
            : FromHealth(await SafeCheckHealthAsync(AppSettings.ManagedGpuServiceUrl, PostStartProbeTimeout, cancellationToken));

        if (readiness.State == ContainerizedProbeState.Available)
        {
            State = ManagedHostState.Ready;
            FailureReason = null;
            _log.Info($"Managed GPU host ready: trigger={trigger}, compute_type={computeType}");
            return new ContainerizedStartResult(
                true,
                true,
                $"Managed local GPU host ready at {AppSettings.ManagedGpuServiceUrl} (compute_type={computeType}).");
        }

        return Fail(
            $"Managed local GPU host failed to become ready at {AppSettings.ManagedGpuServiceUrl}: {readiness.ErrorDetail ?? readiness.State.ToString()}");
    }

    private async Task RunUvBootstrapAsync(
        string uvPath,
        string venvDir,
        string pythonPath,
        string requirementsPath,
        string constraintsPath,
        CancellationToken cancellationToken)
    {
        _log.Info($"Bootstrapping managed GPU runtime at {venvDir}");

        await RunProcessAsync(
            uvPath,
            Path.GetDirectoryName(venvDir) ?? AppContext.BaseDirectory,
            cancellationToken,
            "venv",
            "--python",
            PythonVersion,
            venvDir);

        await RunProcessAsync(
            uvPath,
            AppContext.BaseDirectory,
            cancellationToken,
            "pip",
            "install",
            "--python",
            pythonPath,
            "-r",
            requirementsPath,
            "-c",
            constraintsPath);
    }

    private async Task StartHostProcessAsync(
        string pythonPath,
        string inferenceScriptPath,
        string computeType,
        string hostPidPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_hostProcess is { HasExited: false })
            {
                _log.Info("Managed GPU host process already running; reusing existing process.");
                return;
            }
        }
        catch
        {
        }

        var psi = CreateHostProcessStartInfo(pythonPath, inferenceScriptPath, computeType);

        _log.Info(
            $"Starting managed GPU host: python={pythonPath}, script={inferenceScriptPath}, compute_type={computeType}");

        _hostProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start managed GPU host process.");

        _ = DrainProcessStreamAsync(_hostProcess.StandardOutput, "managed-gpu");
        _ = DrainProcessStreamAsync(_hostProcess.StandardError, "managed-gpu:stderr");

        await File.WriteAllTextAsync(
            hostPidPath,
            _hostProcess.Id.ToString(),
            cancellationToken);
    }

    internal static ProcessStartInfo CreateHostProcessStartInfo(
        string pythonPath,
        string inferenceScriptPath,
        string computeType)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pythonPath,
            WorkingDirectory = Path.GetDirectoryName(inferenceScriptPath) ?? AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add(inferenceScriptPath);
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add("18000");
        psi.ArgumentList.Add("--compute-type");
        psi.ArgumentList.Add(computeType);
        psi.ArgumentList.Add("--require-cuda");
        return psi;
    }

    private static async Task<ManagedGpuRuntimeValidationResult> ValidateManagedGpuRuntimeAsync(
        string pythonPath,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pythonPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(
            "import json, torch; print(json.dumps({'cuda_available': bool(torch.cuda.is_available()), 'cuda_version': getattr(torch.version, 'cuda', None)}))");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start managed GPU runtime validation process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            return new ManagedGpuRuntimeValidationResult(
                false,
                $"Managed Python runtime could not validate torch/CUDA availability: {stderr}".Trim());
        }

        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var cudaAvailable = root.TryGetProperty("cuda_available", out var cudaAvailableElement)
                && cudaAvailableElement.GetBoolean();
            var cudaVersion = root.TryGetProperty("cuda_version", out var cudaVersionElement)
                && cudaVersionElement.ValueKind != JsonValueKind.Null
                ? cudaVersionElement.GetString()
                : null;

            if (cudaAvailable)
            {
                return new ManagedGpuRuntimeValidationResult(
                    true,
                    $"Managed Python runtime can access CUDA{(string.IsNullOrWhiteSpace(cudaVersion) ? string.Empty : $" {cudaVersion}")}.",
                    cudaVersion);
            }

            return new ManagedGpuRuntimeValidationResult(
                false,
                "PyTorch is installed, but torch.cuda.is_available() returned false. Check the installed Torch CUDA build, NVIDIA driver, and whether the managed runtime can see the GPU.",
                cudaVersion);
        }
        catch (Exception ex)
        {
            return new ManagedGpuRuntimeValidationResult(
                false,
                $"Managed Python runtime returned an invalid CUDA validation payload: {ex.Message}. Raw output: {stdout}".Trim());
        }
    }

    private async Task RunProcessAsync(
        string fileName,
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Process '{fileName} {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}: {stderr}");

        if (!string.IsNullOrWhiteSpace(stdout))
            _log.Info(stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr))
            _log.Info(stderr.Trim());
    }

    private async Task DrainProcessStreamAsync(StreamReader reader, string prefix)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
                break;
            if (!string.IsNullOrWhiteSpace(line))
                _log.Info($"[{prefix}] {line}");
        }
    }

    private async Task<ContainerHealthStatus> SafeCheckHealthAsync(
        string serviceUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _healthCheckFunc(serviceUrl, timeout, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.Info($"Managed GPU host health preflight failed for {serviceUrl}: {ex.Message}");
            return ContainerHealthStatus.Unavailable(serviceUrl, ex.Message);
        }
    }

    private static bool ShouldAttemptStart(AppSettings settings, ContainerizedStartupTrigger trigger)
    {
        if (trigger == ContainerizedStartupTrigger.AppStartup && settings.AlwaysStartLocalGpuRuntimeAtAppStart)
            return true;

        return settings.TranscriptionProfile == ComputeProfile.Gpu
            || settings.TranslationProfile == ComputeProfile.Gpu
            || settings.TtsProfile == ComputeProfile.Gpu;
    }

    private ContainerizedStartResult Skip(string message)
    {
        _log.Info(message);
        return new ContainerizedStartResult(false, false, message);
    }

    private ContainerizedStartResult Fail(string message)
    {
        State = ManagedHostState.Failed;
        FailureReason = message;
        _log.Warning(message);
        return new ContainerizedStartResult(true, false, message);
    }

    private static string ComputeBootstrapVersion(
        string inferenceScriptPath,
        string requirementsPath,
        string constraintsPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine(File.ReadAllText(inferenceScriptPath));
        builder.AppendLine(File.ReadAllText(requirementsPath));
        builder.AppendLine(File.ReadAllText(constraintsPath));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes);
    }

    private static string ResolveInferenceScriptPath() =>
        Path.Combine(AppContext.BaseDirectory, "inference", "main.py");

    private static string ResolveRequirementsPath() =>
        Path.Combine(AppContext.BaseDirectory, "inference", "gpu-requirements.txt");

    private static string ResolveConstraintsPath() =>
        Path.Combine(AppContext.BaseDirectory, "inference", "gpu-constraints.txt");

    private static ContainerizedProbeResult FromHealth(ContainerHealthStatus health) =>
        new(
            health.ServiceUrl,
            health.IsAvailable ? ContainerizedProbeState.Available : ContainerizedProbeState.Unavailable,
            DateTimeOffset.UtcNow,
            health.ErrorMessage,
            health.CudaAvailable,
            health.CudaVersion,
            health.Capabilities);
}
