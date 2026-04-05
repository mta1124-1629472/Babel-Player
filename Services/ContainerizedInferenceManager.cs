using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public enum ContainerizedStartupTrigger
{
    AppStartup,
    SettingsChanged,
    Execution,
    Manual,
}

public sealed record ContainerizedStartResult(
    bool Attempted,
    bool IsReady,
    string Message,
    string? ComposeFilePath = null)
{
    public static readonly ContainerizedStartResult AlreadyRunning = new(false, true, "Already running.");
    public static readonly ContainerizedStartResult Started = new(true, true, "Started.");
}

public interface IContainerizedInferenceManager
{
    void RequestEnsureStarted(AppSettings settings, ContainerizedStartupTrigger trigger);
    Task<ContainerizedStartResult> EnsureStartedAsync(
        AppSettings settings,
        ContainerizedStartupTrigger trigger,
        CancellationToken cancellationToken = default);
}

public sealed record ContainerComposeStartRequest(
    string DockerPath,
    string ComposeFilePath,
    string ServiceName);

public sealed record ContainerComposeStartResult(
    bool Success,
    string StandardOutput,
    string StandardError);

public sealed class ContainerizedInferenceManager(
    AppLog log,
    ContainerizedServiceProbe? probe = null,
    Func<string, TimeSpan, CancellationToken, Task<ContainerHealthStatus>>? healthCheckFunc = null,
    Func<ContainerComposeStartRequest, CancellationToken, Task<ContainerComposeStartResult>>? startComposeFunc = null,
    Func<string?>? dockerResolver = null,
    Func<string?>? composeFileResolver = null) : IContainerizedInferenceManager
{
    private const string InferenceServiceName = "inference";
    private static readonly TimeSpan PreflightHealthTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PostStartProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly AppLog _log = log;
    private readonly ContainerizedServiceProbe? _probe = probe;
    private readonly Func<string, TimeSpan, CancellationToken, Task<ContainerHealthStatus>> _healthCheckFunc = healthCheckFunc ?? ContainerizedInferenceClient.CheckHealthAsync;
    private readonly Func<ContainerComposeStartRequest, CancellationToken, Task<ContainerComposeStartResult>> _startComposeFunc = startComposeFunc ?? StartComposeAsync;
    private readonly Func<string?> _dockerResolver = dockerResolver ?? DependencyLocator.FindDocker;
    private readonly Func<string?> _composeFileResolver = composeFileResolver ?? ResolveComposeFilePath;
    private readonly System.Threading.Lock _gate = new();
    private Task<ContainerizedStartResult>? _inFlightStartTask;

    public void RequestEnsureStarted(AppSettings settings, ContainerizedStartupTrigger trigger)
    {
        BackgroundTaskObserver.Observe(
            EnsureStartedAsync(settings, trigger),
            _log,
            "Container autostart");
    }

    public async Task<ContainerizedStartResult> EnsureStartedAsync(
        AppSettings settings,
        ContainerizedStartupTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!ShouldAttemptStart(settings, trigger))
            return Skip($"Container autostart skipped for trigger {trigger}: no containerized runtime requested.");

        var serviceUrl = settings.EffectiveContainerizedServiceUrl;
        if (!IsLoopbackServiceUrl(serviceUrl))
            return Skip($"Container autostart skipped because service URL is not local loopback: {serviceUrl}");

        var preflight = await SafeCheckHealthAsync(serviceUrl, PreflightHealthTimeout, cancellationToken);
        if (preflight.IsAvailable)
        {
            _log.Info($"Container autostart skipped: service already healthy at {serviceUrl}.");
            return new ContainerizedStartResult(false, true, $"Containerized inference service already available at {serviceUrl}.");
        }

        Task<ContainerizedStartResult> task;
        lock (_gate)
        {
            if (_inFlightStartTask is not null)
            {
                _log.Info($"Container autostart reusing in-flight start task for {serviceUrl} (trigger={trigger}).");
                task = _inFlightStartTask;
            }
            else
            {
                _inFlightStartTask = EnsureStartedCoreSafeAsync(serviceUrl, trigger, cancellationToken);
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

    private async Task<ContainerizedStartResult> EnsureStartedCoreAsync(
        string serviceUrl,
        ContainerizedStartupTrigger trigger,
        CancellationToken cancellationToken)
    {
        var dockerPath = _dockerResolver();
        if (string.IsNullOrWhiteSpace(dockerPath))
            return Skip("Docker CLI not found on PATH; local container autostart is unavailable.");

        var composeFilePath = _composeFileResolver();
        if (string.IsNullOrWhiteSpace(composeFilePath))
            return Skip("docker-compose.yml not found near the app; local container autostart is unavailable.");

        _log.Info(
            $"Container autostart requested: trigger={trigger}, url={serviceUrl}, compose={composeFilePath}");

        var startResult = await _startComposeFunc(
            new ContainerComposeStartRequest(dockerPath, composeFilePath, InferenceServiceName),
            cancellationToken);

        if (!startResult.Success)
        {
            var detail = string.IsNullOrWhiteSpace(startResult.StandardError)
                ? startResult.StandardOutput
                : startResult.StandardError;
            _log.Warning($"Container autostart failed: {detail}");
            return new ContainerizedStartResult(
                true,
                false,
                $"Failed to start local containerized inference service: {detail}",
                composeFilePath);
        }

        ContainerizedStartResult readinessResult;
        if (_probe is not null)
        {
            var probeResult = await _probe.WaitForProbeAsync(
                serviceUrl,
                forceRefresh: true,
                waitTimeout: PostStartProbeTimeout,
                cancellationToken);

            readinessResult = probeResult.State == ContainerizedProbeState.Available
                ? new ContainerizedStartResult(
                    true,
                    true,
                    $"Started local containerized inference service from {composeFilePath}.",
                    composeFilePath)
                : new ContainerizedStartResult(
                    true,
                    false,
                    $"Container start requested from {composeFilePath}, but the service is still warming up or unavailable.",
                    composeFilePath);
        }
        else
        {
            var health = await SafeCheckHealthAsync(serviceUrl, PostStartProbeTimeout, cancellationToken);
            readinessResult = health.IsAvailable
                ? new ContainerizedStartResult(
                    true,
                    true,
                    $"Started local containerized inference service from {composeFilePath}.",
                    composeFilePath)
                : new ContainerizedStartResult(
                    true,
                    false,
                    $"Container start requested from {composeFilePath}, but the service is still warming up or unavailable.",
                    composeFilePath);
        }

        _log.Info(
            $"Container autostart result: trigger={trigger}, ready={readinessResult.IsReady}, message={readinessResult.Message}");
        return readinessResult;
    }

    private async Task<ContainerizedStartResult> EnsureStartedCoreSafeAsync(
        string serviceUrl,
        ContainerizedStartupTrigger trigger,
        CancellationToken cancellationToken)
    {
        try
        {
            return await EnsureStartedCoreAsync(serviceUrl, trigger, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("Container autostart failed unexpectedly.", ex);
            return new ContainerizedStartResult(
                true,
                false,
                $"Local containerized inference startup failed unexpectedly: {ex.Message}");
        }
    }

    private static bool ShouldAttemptStart(AppSettings settings, ContainerizedStartupTrigger trigger)
    {
        if (trigger == ContainerizedStartupTrigger.AppStartup && settings.AlwaysRunContainerAtAppStart)
            return true;

        return settings.TranscriptionRuntime == InferenceRuntime.Containerized
            || settings.TranslationRuntime == InferenceRuntime.Containerized
            || settings.TtsRuntime == InferenceRuntime.Containerized;
    }

    private static bool IsLoopbackServiceUrl(string? serviceUrl)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri))
            return false;

        if (uri.IsLoopback)
            return true;

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
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
            _log.Info($"Container health preflight failed for {serviceUrl}: {ex.Message}");
            return ContainerHealthStatus.Unavailable(serviceUrl, ex.Message);
        }
    }

    private ContainerizedStartResult Skip(string message)
    {
        _log.Info(message);
        return new ContainerizedStartResult(false, false, message);
    }

    private static async Task<ContainerComposeStartResult> StartComposeAsync(
        ContainerComposeStartRequest request,
        CancellationToken cancellationToken)
    {
        var workingDirectory = Path.GetDirectoryName(request.ComposeFilePath)
            ?? AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = request.DockerPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        psi.ArgumentList.Add("compose");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(request.ComposeFilePath);
        psi.ArgumentList.Add("up");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(request.ServiceName);

        using var process = Process.Start(psi);
        if (process is null)
            return new ContainerComposeStartResult(false, string.Empty, "Failed to start docker compose process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new ContainerComposeStartResult(
            process.ExitCode == 0,
            stdout.Trim(),
            stderr.Trim());
    }

    private static string? ResolveComposeFilePath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 6; depth++, current = current.Parent)
        {
            foreach (var candidateName in new[] { "docker-compose.yml", "compose.yml", "compose.yaml" })
            {
                var candidate = Path.Combine(current.FullName, candidateName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
