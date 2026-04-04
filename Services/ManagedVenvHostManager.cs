using System;
using System.Collections.Generic;
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
    private static readonly TimeSpan PostStartProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VenvUnlockTimeout = TimeSpan.FromSeconds(5);
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
    private readonly Func<string, string, string, string, string, CancellationToken, Task> _bootstrapRunner;
    private readonly Func<string, CancellationToken, Task<ManagedGpuRuntimeValidationResult>> _runtimeValidator;
    private readonly Func<string, string, string, string, CancellationToken, Task> _hostProcessStarter;
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
        Func<string, string, string, string, string, CancellationToken, Task>? bootstrapRunner = null,
        Func<string, CancellationToken, Task<ManagedGpuRuntimeValidationResult>>? runtimeValidator = null,
        Func<string, string, string, string, CancellationToken, Task>? hostProcessStarter = null)
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
        _bootstrapRunner = bootstrapRunner ?? RunUvBootstrapAsync;
        _runtimeValidator = runtimeValidator ?? ValidateManagedGpuRuntimeAsync;
        _hostProcessStarter = hostProcessStarter ?? StartHostProcessAsync;
    }

    public ManagedHostState State { get; private set; } = ManagedHostState.NotInstalled;

    public string? FailureReason { get; private set; }

    public void RequestEnsureStarted(AppSettings settings, ContainerizedStartupTrigger trigger)
    {
        BackgroundTaskObserver.Observe(
            EnsureStartedAsync(settings, trigger),
            _log,
            "Managed GPU host autostart");
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
        if (preflight.IsAvailable && !IsScriptChangedSinceLastStart())
        {
            State = ManagedHostState.Ready;
            _log.Info("Managed GPU host startup path: reuse");
            return new ContainerizedStartResult(false, true, $"Managed local GPU host already available at {serviceUrl}.");
        }

        if (preflight.IsAvailable)
            _log.Info("Managed GPU host script changed since last start; will stop stale process and restart.");

        if (ShouldDeferRestartForBusyHost(preflight, trigger))
        {
            State = ManagedHostState.Ready;
            _log.Info(
                $"Managed GPU host startup deferred: trigger={trigger}, reason='busy host suspected', detail='{preflight.ErrorMessage ?? "<none>"}'");
            return new ContainerizedStartResult(
                false,
                true,
                $"Managed local GPU host is running but did not answer health probe quickly; deferring restart ({trigger}).");
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
                _inFlightStartTask = EnsureStartedCoreSafeAsync(trigger, cancellationToken);
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
            var runtimeRoot = _runtimeRootResolver();
            var pythonPath = Path.Combine(runtimeRoot, ".venv", "Scripts", "python.exe");
            var hostPidPath = Path.Combine(runtimeRoot, "managed-host.pid");

            RecoverStaleHostProcessesAsync(
                    pythonPath,
                    hostPidPath,
                    stopTrackedProcess: true,
                    cancellationToken: CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch
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
    }

    private async Task<ContainerizedStartResult> EnsureStartedCoreAsync(
        ContainerizedStartupTrigger trigger,
        CancellationToken cancellationToken)
    {
        var hardware = _hardwareSnapshotProvider();
        _log.Info(
            $"Managed GPU host startup requested: trigger={trigger}, " +
            $"cuda={hardware.HasCuda}, gpu='{hardware.GpuName ?? "<none>"}', " +
            $"cuda_version='{hardware.CudaVersion ?? "<none>"}', avx2={hardware.HasAvx2}");
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
        var hostPidPath = Path.Combine(runtimeRoot, "managed-host.pid");
        Directory.CreateDirectory(runtimeRoot);
        _log.Info(
            $"Managed GPU runtime paths: runtime_root={runtimeRoot}, venv_dir={venvDir}, python={pythonPath}, " +
            $"script={inferenceScriptPath}, requirements={requirementsPath}, constraints={constraintsPath}, uv={uvPath}, compute_type={computeType}");

        var bootstrapVersion = ComputeBootstrapVersion(inferenceScriptPath, requirementsPath, constraintsPath);
        var markerPath = Path.Combine(runtimeRoot, ".bootstrap-version");
        var markerValue = File.Exists(markerPath) ? await File.ReadAllTextAsync(markerPath, cancellationToken) : null;
        var needsBootstrap = !File.Exists(pythonPath) || !string.Equals(markerValue, bootstrapVersion, StringComparison.Ordinal);
        _log.Info(
            $"Managed GPU runtime bootstrap state: python_exists={File.Exists(pythonPath)}, marker_exists={File.Exists(markerPath)}, " +
            $"marker_matches={string.Equals(markerValue, bootstrapVersion, StringComparison.Ordinal)}, needs_bootstrap={needsBootstrap}, " +
            $"bootstrap_version={bootstrapVersion}, marker_path={markerPath}");

        if (needsBootstrap)
        {
            State = ManagedHostState.Installing;
            var rebuildExistingRuntime = Directory.Exists(venvDir);
            _log.Info(
                rebuildExistingRuntime
                    ? $"Rebuilding stale or incomplete managed GPU runtime at {venvDir}."
                    : $"Bootstrapping managed GPU runtime at {venvDir}.");
            _log.Info(rebuildExistingRuntime
                ? "Managed GPU host startup path: bootstrap-rebuild"
                : "Managed GPU host startup path: bootstrap-fresh");

            await RecoverStaleHostProcessesAsync(
                pythonPath,
                hostPidPath,
                stopTrackedProcess: true,
                cancellationToken);

            try
            {
                await _bootstrapRunner(
                    uvPath,
                    venvDir,
                    pythonPath,
                    requirementsPath,
                    constraintsPath,
                    cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return Fail(DescribeBootstrapFailure(ex, venvDir));
            }

            await File.WriteAllTextAsync(markerPath, bootstrapVersion, cancellationToken);
            _log.Info($"Managed GPU runtime bootstrap completed at {venvDir}.");
        }
        else
        {
            _log.Info($"Managed GPU runtime already matches bootstrap version at {venvDir}; skipping bootstrap.");
        }

        _log.Info($"Validating managed GPU runtime via {pythonPath}.");
        var runtimeValidation = await _runtimeValidator(pythonPath, cancellationToken);
        if (!runtimeValidation.CudaAvailable)
        {
            return Fail(
                $"Managed local GPU runtime validation failed: {runtimeValidation.Message}");
        }

        _log.Info(
            $"Managed GPU runtime validation passed: {runtimeValidation.Message}");

        var stoppedStaleHost = await RecoverStaleHostProcessesAsync(
            pythonPath,
            hostPidPath,
            stopTrackedProcess: true,
            cancellationToken);

        State = ManagedHostState.Starting;
        _log.Info(stoppedStaleHost
            ? "Managed GPU host startup path: stale-host-stop"
            : "Managed GPU host startup path: fresh-start");
        _log.Info(
            $"Launching managed GPU host process: url={AppSettings.ManagedGpuServiceUrl}, " +
            $"compute_type={computeType}, gpu_architecture={hardware.GpuComputeCapability ?? "<unknown>"}, " +
            $"host_pid_path={hostPidPath}");
            
        // Log FP8 request details if applicable
        if (computeType == "float8")
        {
            _log.Info(
                $"FP8 compute type detected: blackwell_capable={hardware.IsBlackwellCapable}, " +
                $"gpu_compute_capability={hardware.GpuComputeCapability ?? "<unknown>"}, " +
                $"Note: Python host will validate and downgrade to float16 if any stage does not support FP8");
        }
        await _hostProcessStarter(
            pythonPath,
            inferenceScriptPath,
            computeType,
            hostPidPath,
            cancellationToken);

        _log.Info(
            $"Waiting for managed GPU host readiness: url={AppSettings.ManagedGpuServiceUrl}, timeout={PostStartProbeTimeout.TotalSeconds}s");
        var readiness = _probe is not null
            ? await _probe.WaitForProbeAsync(
                AppSettings.ManagedGpuServiceUrl,
                forceRefresh: true,
                waitTimeout: PostStartProbeTimeout,
                cancellationToken)
            : FromHealth(await SafeCheckHealthAsync(AppSettings.ManagedGpuServiceUrl, PostStartProbeTimeout, cancellationToken));

        _log.Info(
            $"Managed GPU host readiness probe result: state={readiness.State}, error='{readiness.ErrorDetail ?? "<none>"}', " +
            $"cuda_available={readiness.CudaAvailable}, cuda_version='{readiness.CudaVersion ?? "<none>"}', " +
            $"capabilities={FormatCapabilities(readiness.Capabilities)}");

        if (readiness.State == ContainerizedProbeState.Available
            && readiness.Capabilities is not null
            && !AllCapabilitiesReady(readiness.Capabilities))
        {
            _log.Info(
                $"Managed GPU host is live while capabilities are still warming: {FormatCapabilities(readiness.Capabilities)}");
        }

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

        return Fail(BuildReadinessFailureMessage(readiness));
    }

    private async Task<ContainerizedStartResult> EnsureStartedCoreSafeAsync(
        ContainerizedStartupTrigger trigger,
        CancellationToken cancellationToken)
    {
        try
        {
            return await EnsureStartedCoreAsync(trigger, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Managed GPU host startup failed unexpectedly.", ex);
            return Fail($"Managed local GPU host startup failed unexpectedly: {ex.Message}");
        }
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
            "--clear",
            "--python",
            PythonVersion,
            venvDir);

        await RunProcessAsync(
            uvPath,
            AppContext.BaseDirectory,
            cancellationToken,
            "pip",
            "install",
            "--index-strategy",
            "unsafe-best-match",
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
        var psi = CreateHostProcessStartInfo(pythonPath, inferenceScriptPath, computeType);

        _log.Info(
            $"Starting managed GPU host: python={pythonPath}, script={inferenceScriptPath}, compute_type={computeType}, require_cuda={RequiresCuda(computeType)}");

        _hostProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start managed GPU host process.");

        _log.Info($"Managed GPU host process started with pid={_hostProcess.Id}.");

        _ = DrainProcessStreamAsync(_hostProcess.StandardOutput, "managed-gpu");
        _ = DrainProcessStreamAsync(_hostProcess.StandardError, "managed-gpu:stderr");

        await File.WriteAllTextAsync(
            hostPidPath,
            _hostProcess.Id.ToString(),
            cancellationToken);
    }

    private async Task<bool> RecoverStaleHostProcessesAsync(
        string pythonPath,
        string hostPidPath,
        bool stopTrackedProcess,
        CancellationToken cancellationToken)
    {
        var stoppedAny = false;

        if (stopTrackedProcess && await StopTrackedHostProcessAsync(cancellationToken))
            stoppedAny = true;

        if (await StopPidFileHostProcessAsync(hostPidPath, pythonPath, cancellationToken))
            stoppedAny = true;

        if (await StopRemainingManagedPythonProcessesAsync(pythonPath, cancellationToken))
            stoppedAny = true;

        await DeletePidFileIfPresentAsync(hostPidPath);
        await WaitForVenvUnlockAsync(pythonPath, cancellationToken);
        return stoppedAny;
    }

    private async Task<bool> StopTrackedHostProcessAsync(CancellationToken cancellationToken)
    {
        Process? trackedProcess;
        try
        {
            trackedProcess = _hostProcess is { HasExited: false } ? _hostProcess : null;
        }
        catch
        {
            trackedProcess = null;
        }

        if (trackedProcess is null)
            return false;

        _log.Warning($"Stopping tracked stale managed GPU host process pid={trackedProcess.Id} before restart.");
        var stopped = await StopProcessAsync(trackedProcess, "tracked host process", cancellationToken);
        if (stopped)
            _hostProcess = null;

        return stopped;
    }

    private async Task<bool> StopPidFileHostProcessAsync(
        string hostPidPath,
        string pythonPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(hostPidPath))
            return false;

        string pidText;
        try
        {
            pidText = (await File.ReadAllTextAsync(hostPidPath, cancellationToken)).Trim();
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to read managed GPU host pid file '{hostPidPath}': {ex.Message}");
            return false;
        }

        if (!int.TryParse(pidText, out var pid) || pid <= 0)
        {
            _log.Warning($"Managed GPU host pid file '{hostPidPath}' contained an invalid pid '{pidText}'. Removing stale pid file.");
            await DeletePidFileIfPresentAsync(hostPidPath);
            return false;
        }

        Process? process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            _log.Info($"Managed GPU host pid file '{hostPidPath}' pointed to exited process pid={pid}. Removing stale pid file.");
            await DeletePidFileIfPresentAsync(hostPidPath);
            return false;
        }

        if (!IsManagedPythonProcess(process, pythonPath))
        {
            _log.Warning(
                $"Managed GPU host pid file '{hostPidPath}' points to unrelated live process pid={pid}; leaving the process alone and replacing pid file on next successful start.");
            return false;
        }

        _log.Warning($"Stopping stale managed GPU host from pid file pid={pid} before restart.");
        var stopped = await StopProcessAsync(process, "pid-file host process", cancellationToken);
        if (stopped)
            await DeletePidFileIfPresentAsync(hostPidPath);

        return stopped;
    }

    private async Task<bool> StopRemainingManagedPythonProcessesAsync(
        string pythonPath,
        CancellationToken cancellationToken)
    {
        var stoppedAny = false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(pythonPath)))
        {
            try
            {
                if (!IsManagedPythonProcess(process, pythonPath))
                    continue;

                _log.Warning($"Stopping stray managed GPU python process pid={process.Id} before restart.");
                if (await StopProcessAsync(process, "managed python process", cancellationToken))
                    stoppedAny = true;
            }
            finally
            {
                process.Dispose();
            }
        }

        return stoppedAny;
    }

    private async Task<bool> StopProcessAsync(
        Process process,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            if (process.HasExited)
                return false;
        }
        catch
        {
            return false;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to stop stale managed GPU {reason} pid={process.Id}: {ex.Message}",
                ex);
        }

        try
        {
            using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shutdownCts.CancelAfter(HostShutdownTimeout);
            await process.WaitForExitAsync(shutdownCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Timed out waiting for stale managed GPU {reason} pid={process.Id} to exit.");
        }

        return true;
    }

    private async Task WaitForVenvUnlockAsync(string pythonPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(pythonPath))
            return;

        var start = Stopwatch.StartNew();
        while (start.Elapsed < VenvUnlockTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(
                    pythonPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Managed local GPU runtime remained locked after stale host shutdown: {pythonPath}");
    }

    private static bool IsManagedPythonProcess(Process process, string pythonPath)
    {
        try
        {
            var processPath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(processPath)
                && string.Equals(processPath, pythonPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task DeletePidFileIfPresentAsync(string hostPidPath)
    {
        if (!File.Exists(hostPidPath))
            return;

        try
        {
            File.Delete(hostPidPath);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to remove managed GPU host pid file '{hostPidPath}': {ex.Message}");
        }
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
        
        // Any GPU compute type requires CUDA at host startup.
        if (RequiresCuda(computeType))
        {
            psi.ArgumentList.Add("--require-cuda");
        }
        
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
            stdout = stdout.Trim();
            stderr = stderr.Trim();
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

        _log.Info(
            $"Running managed GPU process: file={fileName}, working_directory={workingDirectory}, args={string.Join(' ', arguments)}");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        _log.Info(
            $"Managed GPU process exited: file={fileName}, exit_code={process.ExitCode}");

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
            _log.Info($"Managed GPU host health probe starting: url={serviceUrl}, timeout={timeout.TotalSeconds}s");
            var health = await _healthCheckFunc(serviceUrl, timeout, cancellationToken);
            _log.Info(
                $"Managed GPU host health probe finished: available={health.IsAvailable}, " +
                $"cuda_available={health.CudaAvailable}, cuda_version='{health.CudaVersion ?? "<none>"}', " +
                $"error='{health.ErrorMessage ?? "<none>"}'");
            return health;
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

    private string DescribeBootstrapFailure(Exception ex, string venvDir)
    {
        var detail = ex.Message.Trim();
        if (!IsLockedRuntimeFailure(detail))
            return $"Managed local GPU runtime bootstrap failed: {detail}";

        return
            $"Managed local GPU runtime is locked by a stale host process under {venvDir}. " +
            $"The app attempted automatic recovery but the runtime still could not be rebuilt. Details: {detail}";
    }

    private static bool IsLockedRuntimeFailure(string message) =>
        message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
        || message.Contains("failed to remove directory", StringComparison.OrdinalIgnoreCase)
        || message.Contains(".venv\\Scripts", StringComparison.OrdinalIgnoreCase)
        || message.Contains("remained locked", StringComparison.OrdinalIgnoreCase);

    private bool ShouldDeferRestartForBusyHost(
        ContainerHealthStatus preflight,
        ContainerizedStartupTrigger trigger)
    {
        if (trigger != ContainerizedStartupTrigger.AppStartup
            && trigger != ContainerizedStartupTrigger.SettingsChanged)
        {
            return false;
        }

        if (!IsTrackedHostProcessRunning())
            return false;

        return IsTransientBusyHealthFailure(preflight.ErrorMessage);
    }

    private bool IsTrackedHostProcessRunning()
    {
        try
        {
            return _hostProcess is { HasExited: false };
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTransientBusyHealthFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;

        return error.Contains("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase)
            || error.Contains("request was canceled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildReadinessFailureMessage(ContainerizedProbeResult readiness)
    {
        var detail = readiness.ErrorDetail ?? readiness.State.ToString();
        try
        {
            if (_hostProcess is { HasExited: true } hostProcess)
                detail = $"managed host exited before readiness probe completed with exit code {hostProcess.ExitCode}";
        }
        catch
        {
        }

        return $"Managed local GPU host failed to become ready at {AppSettings.ManagedGpuServiceUrl}: {detail}";
    }

    private static bool AllCapabilitiesReady(ContainerCapabilitiesSnapshot capabilities) =>
        capabilities.TranscriptionReady
        && capabilities.TranslationReady
        && capabilities.TtsReady;

    private static string FormatCapabilities(ContainerCapabilitiesSnapshot? capabilities)
    {
        if (capabilities is null)
            return "<none>";

        return
            $"tx={capabilities.TranscriptionReady}('{capabilities.TranscriptionDetail ?? "<none>"}'), " +
            $"tl={capabilities.TranslationReady}('{capabilities.TranslationDetail ?? "<none>"}'), " +
            $"tts={capabilities.TtsReady}('{capabilities.TtsDetail ?? "<none>"}')";
    }

    private bool IsScriptChangedSinceLastStart()
    {
        try
        {
            var runtimeRoot = _runtimeRootResolver();
            var markerPath = Path.Combine(runtimeRoot, ".bootstrap-version");
            if (!File.Exists(markerPath))
                return true; // no record of what's running — assume changed
            var storedHash = File.ReadAllText(markerPath).Trim();
            var currentHash = ComputeBootstrapVersion(
                _inferenceScriptResolver(),
                _requirementsPathResolver(),
                _constraintsPathResolver());
            return !string.Equals(storedHash, currentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false; // can't determine — assume unchanged to avoid spurious restarts
        }
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

    private static bool RequiresCuda(string computeType) =>
        string.Equals(computeType, "float16", StringComparison.OrdinalIgnoreCase)
        || string.Equals(computeType, "float8", StringComparison.OrdinalIgnoreCase);
}
