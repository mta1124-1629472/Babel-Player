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
    private static readonly TimeSpan PreflightHealthTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PreflightStabilizationWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PreflightRetryDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PostStartProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VenvUnlockTimeout = TimeSpan.FromSeconds(5);
    // Pin the full Python patch version so uv resolves a reproducible interpreter build.
    // Python 3.12 is required for the managed GPU runtime compatibility path.
    private const string PythonVersion = "3.12";

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
    private readonly ContainerizedRequestLeaseTracker? _requestLeaseTracker;
    private readonly TimeSpan _postStartProbeTimeout;
    private readonly Lock _gate = new();
    private Task<ContainerizedStartResult>? _inFlightStartTask;
    private Process? _hostProcess;

    /// <summary>
    /// Initializes a ManagedVenvHostManager with optional dependency overrides and runtime configuration.
    /// </summary>
    /// <param name="log">Application logger used for operational messages.</param>
    /// <param name="probe">Optional readiness probe used to wait for the hosted service to become available.</param>
    /// <param name="healthCheckFunc">Optional function to perform health checks against the managed host service.</param>
    /// <param name="hardwareSnapshotProvider">Optional provider for the local hardware capability snapshot.</param>
    /// <param name="uvResolver">Optional resolver that returns the path to the uv executable.</param>
    /// <param name="runtimeRootResolver">Optional resolver that returns the runtime root directory for the managed venv.</param>
    /// <param name="inferenceScriptResolver">Optional resolver that returns the path to the inference script.</param>
    /// <param name="requirementsPathResolver">Optional resolver that returns the path to the pip requirements file used for bootstrapping.</param>
    /// <param name="constraintsPathResolver">Optional resolver that returns the path to the pip constraints file used for bootstrapping.</param>
    /// <param name="bootstrapRunner">Optional bootstrap runner delegate that creates or rebuilds the Python venv and installs dependencies.</param>
    /// <param name="runtimeValidator">Optional validator delegate that verifies the runtime (e.g., CUDA/PyTorch availability) for the given python interpreter.</param>
    /// <param name="hostProcessStarter">Optional delegate that starts the managed Python host process.</param>
    /// <param name="requestLeaseTracker">Optional tracker used to detect active local requests for defer/cleanup decisioning.</param>
    /// <param name="postStartProbeTimeout">Optional timeout used when waiting for the host to become ready after startup.</param>
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
        Func<string, string, string, string, CancellationToken, Task>? hostProcessStarter = null,
        ContainerizedRequestLeaseTracker? requestLeaseTracker = null,
        TimeSpan? postStartProbeTimeout = null)
    {
        _log = log;
        _probe = probe;
        _healthCheckFunc = healthCheckFunc ?? ContainerizedInferenceClient.CheckHealthAsync;
        _hardwareSnapshotProvider = hardwareSnapshotProvider ?? (() => HardwareSnapshot.Run());
        _uvResolver = uvResolver ?? DependencyLocator.FindUv;
        _runtimeRootResolver = runtimeRootResolver ?? ManagedRuntimeLayout.GetRuntimeRoot;
        _inferenceScriptResolver = inferenceScriptResolver ?? ResolveInferenceScriptPath;
        _requirementsPathResolver = requirementsPathResolver ?? ResolveRequirementsPath;
        _constraintsPathResolver = constraintsPathResolver ?? ResolveConstraintsPath;
        _bootstrapRunner = bootstrapRunner ?? RunUvBootstrapAsync;
        _runtimeValidator = runtimeValidator ?? ValidateManagedGpuRuntimeAsync;
        _hostProcessStarter = hostProcessStarter ?? StartHostProcessAsync;
        _requestLeaseTracker = requestLeaseTracker;
        _postStartProbeTimeout = postStartProbeTimeout ?? PostStartProbeTimeout;
    }

    public ManagedHostState State { get; private set; } = ManagedHostState.NotInstalled;

    public string? FailureReason { get; private set; }

    /// <summary>
    /// The most recent status line from the bootstrap process (e.g., "Downloading torch (2.4 GB)").
    /// Updated live during installation.
    /// </summary>
    public string BootstrapStatusLine { get; private set; } = string.Empty;

    public void RequestEnsureStarted(AppSettings settings, ContainerizedStartupTrigger trigger)
    {
        BackgroundTaskObserver.Observe(
            EnsureStartedAsync(settings, trigger),
            _log,
            "Managed GPU host autostart");
    }

    /// <summary>
    /// Returns the current probe status for the managed local GPU host, or starts a background probe if none is in flight.
    /// </summary>
    /// <param name="settings">Application settings used to validate that the probe has been initialized.</param>
    /// <returns>
    /// A <see cref="ContainerizedProbeResult"/> describing the current host state, capabilities, and any error detail.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="settings"/> is null.</exception>
    public ContainerizedProbeResult GetCurrentStatus(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (_probe is null)
        {
            return new ContainerizedProbeResult(
                AppSettings.ManagedGpuServiceUrl,
                ContainerizedProbeState.Unavailable,
                DateTimeOffset.UtcNow,
                "Managed host probe not initialized.");
        }

        return _probe.GetCurrentOrStartBackgroundProbe(AppSettings.ManagedGpuServiceUrl);
    }

    /// <summary>
    /// Orchestrates ensuring the managed local GPU host is running and ready, reusing an existing host, deferring restart when busy, or performing a restart/bootstrap as needed.
    /// </summary>
    /// <param name="settings">Application settings used to decide whether to attempt and how to start the managed GPU host.</param>
    /// <param name="trigger">The startup trigger that initiated this ensure-start attempt.</param>
    /// <param name="cancellationToken">Token to observe for cancellation of the start operation.</param>
    /// <returns>A <see cref="ContainerizedStartResult"/> describing whether a start was attempted, whether the host is available (reused or started), and a human-readable message explaining the outcome.</returns>
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
        var scriptChangedSinceLastStart = IsScriptChangedSinceLastStart();
        var preflight = await SafeCheckHealthAsync(serviceUrl, PreflightHealthTimeout, cancellationToken);
        preflight = await StabilizeTrackedHostHealthAsync(serviceUrl, preflight, cancellationToken);

        if (preflight.IsAvailable && !scriptChangedSinceLastStart)
        {
            State = ManagedHostState.Ready;
            _log.Info("Managed GPU host startup path: reuse");
            return new ContainerizedStartResult(false, true, $"Managed local GPU host already available at {serviceUrl}.");
        }

        if (ShouldDeferRestartForBusyHost(preflight))
        {
            var canReuseTrackedHost =
                preflight.IsAvailable
                || HasActiveLocalRequests()
                || (IsTrackedHostProcessRunning() && IsTransientBusyHealthFailure(preflight.ErrorMessage));

            if (canReuseTrackedHost)
            {
                State = ManagedHostState.Ready;
                var deferMessage = BuildBusyHostDeferMessage(serviceUrl, preflight);
                _log.Info(
                    $"Managed GPU host startup deferred: trigger={trigger}, active_requests={_requestLeaseTracker?.ActiveRequests ?? 0}, " +
                    $"host_busy={preflight.Busy}, reason='{deferMessage}'");
                return new ContainerizedStartResult(false, true, deferMessage);
            }
            else
            {
                // Host is busy but not available; do not mark Ready or return healthy.
                var notAvailableMessage = $"Managed GPU host is not available: {preflight.ErrorMessage ?? "unknown error"}";
                _log.Warning($"Managed GPU host startup path: host reported busy but is not available.");
                return new ContainerizedStartResult(false, false, notAvailableMessage);
            }
        }

        if (preflight.IsAvailable)
            _log.Info("Managed GPU host script changed since last start; will stop stale process and restart.");

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
            var venvDir = Path.Combine(runtimeRoot, ".venv");
            var pythonPath = OperatingSystem.IsWindows()
                ? Path.Combine(venvDir, "Scripts", "python.exe")
                : Path.Combine(venvDir, "bin", "python");
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
        var pythonPath = OperatingSystem.IsWindows()
            ? Path.Combine(venvDir, "Scripts", "python.exe")
            : Path.Combine(venvDir, "bin", "python");
        var hostPidPath = Path.Combine(runtimeRoot, "managed-host.pid");
        Directory.CreateDirectory(runtimeRoot);
        _log.Info(
            $"Managed GPU runtime paths: runtime_root={runtimeRoot}, venv_dir={venvDir}, python={pythonPath}, " +
            $"script={inferenceScriptPath}, requirements={requirementsPath}, constraints={constraintsPath}, uv={uvPath}, compute_type={computeType}");

        var bootstrapVersion = ComputeBootstrapVersion(requirementsPath, constraintsPath);
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
        ManagedGpuRuntimeValidationResult runtimeValidation;
        try
        {
            runtimeValidation = await _runtimeValidator(pythonPath, cancellationToken);
        }
        catch (Exception ex)
        {
            // If the runtime validator itself throws an exception, preserve the error message
            // and return a failure that includes the validation context
            return Fail($"Managed local GPU runtime validation failed: {ex.Message}");
        }

        if (!runtimeValidation.CudaAvailable)
        {
            var isMissingTorch = runtimeValidation.Message.Contains("missing PyTorch", StringComparison.OrdinalIgnoreCase);
            if (isMissingTorch)
            {
                _log.Warning($"Managed GPU runtime validation detected missing PyTorch; attempting auto-rebuild.");
                var rebuildMarkerPath = Path.Combine(runtimeRoot, ".bootstrap-version");
                try
                {
                    if (File.Exists(rebuildMarkerPath))
                    {
                        File.Delete(rebuildMarkerPath);
                        _log.Info($"Cleared bootstrap marker at {rebuildMarkerPath} to trigger rebuild.");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning($"Failed to clear bootstrap marker: {ex.Message}");
                }

                await RecoverStaleHostProcessesAsync(
                    pythonPath,
                    hostPidPath,
                    stopTrackedProcess: true,
                    cancellationToken);

                State = ManagedHostState.Installing;
                _log.Info($"Auto-rebuilding managed GPU runtime at {venvDir} due to missing PyTorch.");
                try
                {
                    await _bootstrapRunner(
                        uvPath,
                        venvDir,
                        pythonPath,
                        requirementsPath,
                        constraintsPath,
                        cancellationToken);
                    await File.WriteAllTextAsync(rebuildMarkerPath, bootstrapVersion, cancellationToken);
                    _log.Info($"Auto-rebuild completed at {venvDir}.");
                }
                catch (InvalidOperationException ex)
                {
                    return Fail(DescribeBootstrapFailure(ex, venvDir));
                }

                _log.Info($"Re-validating managed GPU runtime after auto-rebuild.");
                try
                {
                    runtimeValidation = await _runtimeValidator(pythonPath, cancellationToken);
                }
                catch (Exception ex)
                {
                    return Fail($"Managed local GPU runtime validation failed after auto-rebuild: {ex.Message}");
                }
                
                if (!runtimeValidation.CudaAvailable)
                {
                    return Fail(
                        $"Managed local GPU runtime validation failed after auto-rebuild: {runtimeValidation.Message}");
                }
                _log.Info($"Managed GPU runtime validation passed after auto-rebuild: {runtimeValidation.Message}");
            }
            else
            {
                return Fail(
                    $"Managed local GPU runtime validation failed: {runtimeValidation.Message}");
            }
        }
        else
        {
            _log.Info(
                $"Managed GPU runtime validation passed: {runtimeValidation.Message}");
        }

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

        // Record which script version is running so IsScriptChangedSinceLastStart can detect edits
        var scriptVersionPath = Path.Combine(runtimeRoot, ".script-version");
        await File.WriteAllTextAsync(scriptVersionPath, ComputeScriptVersion(inferenceScriptPath), cancellationToken);

        _log.Info(
            $"Waiting for managed GPU host readiness: url={AppSettings.ManagedGpuServiceUrl}, timeout={_postStartProbeTimeout.TotalSeconds}s");
        var readiness = _probe is not null
            ? await _probe.WaitForProbeAsync(
                AppSettings.ManagedGpuServiceUrl,
                forceRefresh: true,
                waitTimeout: _postStartProbeTimeout,
                cancellationToken)
            : FromHealth(await SafeCheckHealthAsync(AppSettings.ManagedGpuServiceUrl, _postStartProbeTimeout, cancellationToken));

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

    /// <summary>
    /// Optional callback invoked with each live output line during uv bootstrap.
    /// Set before calling <see cref="RequestEnsureStarted"/> to receive progress.
    /// </summary>
    public Action<string>? BootstrapProgressCallback { get; set; }

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
            null,
            cancellationToken,
            "venv",
            "--clear",
            "--python",
            PythonVersion,
            venvDir);

        await RunProcessAsync(
            uvPath,
            AppContext.BaseDirectory,
            line =>
            {
                BootstrapStatusLine = line;
                BootstrapProgressCallback?.Invoke(line);
            },
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

    /// <summary>
    /// Attempts to stop any stale managed GPU host processes and wait for the virtual environment to be unlocked.
    /// </summary>
    /// <param name="pythonPath">Full path to the managed Python executable used to identify matching processes.</param>
    /// <param name="hostPidPath">Path to the pid file that may reference a running host process; the file will be deleted if present.</param>
    /// <param name="stopTrackedProcess">
    /// If true, also attempts to stop the currently tracked host process instance held by this manager.
    /// If false, the tracked process will not be stopped.
    /// </param>
    /// <param name="cancellationToken">Token to observe for cancellation while stopping processes and waiting for venv unlock.</param>
    /// <returns>`true` if any managed host process was stopped, `false` if none were stopped or recovery was skipped due to active local requests.</returns>
    private async Task<bool> RecoverStaleHostProcessesAsync(
        string pythonPath,
        string hostPidPath,
        bool stopTrackedProcess,
        CancellationToken cancellationToken)
    {
        IDisposable? recoveryToken = null;
        if (_requestLeaseTracker is not null)
        {
            // Begin recovery gate to block new leases and wait for existing ones to drain.
            recoveryToken = _requestLeaseTracker.BeginRecovery();

            try
            {
                await _requestLeaseTracker.WaitForZeroActiveRequestsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.Warning("Managed GPU host recovery canceled while waiting for active requests to complete.");
                throw;
            }
        }

        try
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
        finally
        {
            recoveryToken?.Dispose();
        }
    }

    /// <summary>
    /// Stops the currently tracked managed GPU host process if one is running.
    /// </summary>
    /// <returns>`true` if a tracked process was stopped, `false` otherwise.</returns>
    /// <remarks>
    /// This method should be called only during recovery when the recovery gate is active and all active requests have completed.
    /// </remarks>
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
        int pid;
        try
        {
            if (process.HasExited)
                return false;
            pid = process.Id;
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
            _log.Warning($"process.Kill() failed for stale managed GPU {reason} pid={pid}: {ex.Message}. Trying taskkill fallback.");
        }

        // Wait for the process to exit
        var exited = await WaitForProcessExitAsync(process, HostShutdownTimeout, cancellationToken);
        if (exited)
            return true;

        // Process didn't die — escalate to taskkill /F /T (kills the whole process tree forcibly)
        _log.Warning($"Stale managed GPU {reason} pid={pid} did not exit after Kill(); escalating to taskkill /F /T.");
        try
        {
            using var taskkill = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/F /T /PID {pid}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            taskkill?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _log.Warning($"taskkill /F /T /PID {pid} failed: {ex.Message}");
        }

        exited = await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(5), cancellationToken);
        if (!exited)
            throw new InvalidOperationException(
                $"Timed out waiting for stale managed GPU {reason} pid={pid} to exit even after taskkill /F /T.");

        return true;
    }

    private static async Task<bool> WaitForProcessExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task WaitForVenvUnlockAsync(string pythonPath, CancellationToken cancellationToken)
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

        // Make CUDA kernel errors synchronous so the Python traceback points at the
        // actual failing op rather than a random later API call.
        psi.Environment["CUDA_LAUNCH_BLOCKING"] = "1";

        // Redirect the HuggingFace hub model cache into the app data directory.
        // This isolates our models from the user's global HF cache and prevents
        // the "Unable to open file 'model.bin'" failure that occurs on Windows when
        // huggingface_hub is upgraded between releases and fails to re-validate
        // cache files created by a prior version.
        psi.Environment["HUGGINGFACE_HUB_CACHE"] = ManagedRuntimeLayout.GetModelCacheDir();
        psi.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
        psi.Environment[QwenRuntimePolicy.MaxConcurrencyEnvironmentVariable] =
            QwenRuntimePolicy.ResolveMaxConcurrency().ToString();

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
            var stderrLower = stderr.ToLowerInvariant();
            var message = stderrLower.Contains("no module named 'torch'")
                ? "The managed Python runtime is missing PyTorch. The virtual environment may be incomplete or corrupted. " +
                  "Delete the .venv folder in the runtime directory and restart the app to trigger a fresh bootstrap."
                : $"Managed Python runtime could not validate torch/CUDA availability: {stderr}".Trim();
            return new ManagedGpuRuntimeValidationResult(false, message);
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
        Action<string>? onStatusLine,
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

        // Stream stdout line-by-line for live progress, accumulate stderr for error reporting.
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutLines = new System.Text.StringBuilder();
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            _log.Info(line);
            onStatusLine?.Invoke(line);
            if (!string.IsNullOrWhiteSpace(line))
                stdoutLines.AppendLine(line);
        }

        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;

        _log.Info(
            $"Managed GPU process exited: file={fileName}, exit_code={process.ExitCode}");

        if (process.ExitCode != 0)
        {
            var stdoutSnippet = stdoutLines.Length > 0 ? $"\nstdout: {stdoutLines}" : string.Empty;
            throw new InvalidOperationException(
                $"Process '{fileName} {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}: {stderr}{stdoutSnippet}");
        }

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

    /// <summary>
    /// Probes the managed GPU host at the given URL and returns its health status; exceptions are converted into an unavailable status.
    /// </summary>
    /// <param name="serviceUrl">The service URL to probe (e.g., http://127.0.0.1:18000).</param>
    /// <param name="timeout">Maximum duration to wait for the health probe.</param>
    /// <param name="cancellationToken">Token to cancel the probe.</param>
    /// <returns>The observed <see cref="ContainerHealthStatus"/>; if an error occurs, returns an unavailable status whose message contains the error.</returns>
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
                $"Managed GPU host health probe finished: status='{health.StatusLine}', available={health.IsAvailable}, " +
                $"cuda_available={health.CudaAvailable}, cuda_version='{health.CudaVersion ?? "<none>"}', " +
                $"busy={health.Busy}, active_requests={health.ActiveRequests}, active_qwen={health.ActiveQwenRequests}, " +
                $"active_diarization={health.ActiveDiarizationRequests}, error='{health.ErrorMessage ?? "<none>"}', " +
                $"capabilities_error='{health.CapabilitiesError ?? "<none>"}'");
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

    private static string DescribeBootstrapFailure(Exception ex, string venvDir)
    {
        var detail = ex.Message.Trim();
        if (!IsLockedRuntimeFailure(detail))
            return $"Managed local GPU runtime bootstrap failed: {detail}";

        return
            $"Managed local GPU runtime is locked by a stale host process under {venvDir}. " +
            $"The app attempted automatic recovery but the runtime still could not be rebuilt. Details: {detail}";
    }

    /// <summary>
        /// Determines whether a bootstrap/runtime failure message indicates a locked or permission-related runtime directory.
        /// </summary>
        /// <param name="message">The failure message text to inspect.</param>
        /// <returns>`true` if the message suggests the runtime directory was locked or inaccessible, `false` otherwise.</returns>
        private static bool IsLockedRuntimeFailure(string message) =>
        message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
        || message.Contains("failed to remove directory", StringComparison.OrdinalIgnoreCase)
        || message.Contains("remained locked", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Waits for a tracked managed host to stabilize by repeatedly rechecking its health within a short stabilization window.
    /// </summary>
    /// <param name="serviceUrl">The URL of the managed host health endpoint to probe.</param>
    /// <param name="initialHealth">The initial health result to base stabilization on.</param>
    /// <returns>The final <see cref="ContainerHealthStatus"/> observed: an available status if the host becomes ready, otherwise the most recent health (which may indicate busy or unavailable).</returns>
    private async Task<ContainerHealthStatus> StabilizeTrackedHostHealthAsync(
        string serviceUrl,
        ContainerHealthStatus initialHealth,
        CancellationToken cancellationToken)
    {
        if (initialHealth.IsAvailable)
            return initialHealth;

        if (!IsTrackedHostProcessRunning() && !HasActiveLocalRequests())
            return initialHealth;

        var lastHealth = initialHealth;
        var deadline = DateTimeOffset.UtcNow + PreflightStabilizationWindow;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ShouldDeferRestartForBusyHost(lastHealth))
                return lastHealth;

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(PreflightRetryDelay, cancellationToken).ConfigureAwait(false);
            lastHealth = await SafeCheckHealthAsync(serviceUrl, PreflightHealthTimeout, cancellationToken).ConfigureAwait(false);
            if (lastHealth.IsAvailable)
                return lastHealth;
        }

        return lastHealth;
    }

    /// <summary>
    /// Determines whether a managed-host restart should be deferred because the host is busy or there are active local requests.
    /// </summary>
    /// <param name="preflight">The preflight health probe result for the tracked host.</param>
    /// <returns>`true` if restart should be deferred due to active local requests, the host reporting busy, or the tracked host running with a transient busy health failure; `false` otherwise.</returns>
    private bool ShouldDeferRestartForBusyHost(ContainerHealthStatus preflight)
    {
        if (HasActiveLocalRequests())
            return true;

        if (HasHostReportedActiveWork(preflight))
            return true;

        if (!IsTrackedHostProcessRunning())
            return false;

        return IsTransientBusyHealthFailure(preflight.ErrorMessage);
    }

    private static bool HasHostReportedActiveWork(ContainerHealthStatus preflight)
    {
        if (preflight.Busy
            || preflight.ActiveRequests > 0
            || preflight.ActiveQwenRequests > 0
            || preflight.ActiveDiarizationRequests > 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(preflight.BusyReason))
            return true;

        if (preflight.ProviderHealth is null)
            return false;

        foreach (var snapshot in preflight.ProviderHealth.Values)
        {
            if (snapshot is not null && IsProviderWarmupInProgress(snapshot))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Indicates whether there are currently active local requests tracked by the optional request lease tracker.
    /// </summary>
    /// <returns><c>true</c> if a request lease tracker is present and reports active requests; <c>false</c> otherwise.</returns>
    private bool HasActiveLocalRequests() => _requestLeaseTracker?.HasActiveRequests == true;

    /// <summary>
    /// Determines whether the currently tracked host process exists and has not exited.
    /// </summary>
    /// <returns>`true` if a tracked host process is present and has not exited; `false` if there is no tracked process, it has exited, or the process status cannot be determined.</returns>
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

    /// <summary>
    /// Detects whether an error message represents a transient busy or timeout condition reported by the health probe.
    /// </summary>
    /// <param name="error">The error message text to inspect.</param>
    /// <returns>`true` if the text indicates a transient timeout or cancellation (for example: timeout, HttpClient timeout, or operation/request canceled); `false` otherwise.</returns>
    private static bool IsTransientBusyHealthFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;

        return error.Contains("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase)
            || error.Contains("request was canceled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProviderWarmupInProgress(ContainerProviderHealthSnapshot snapshot)
    {
        if (string.Equals(snapshot.State, "warming", StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.State, "refreshing", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(snapshot.Detail)
                && !snapshot.Detail.Contains("failed", StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(snapshot.Detail)
            && snapshot.Detail.Contains("in progress", StringComparison.OrdinalIgnoreCase)
            && !snapshot.Detail.Contains("failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a human-readable message explaining why a restart of the managed local GPU host is being deferred.
    /// </summary>
    /// <param name="serviceUrl">The service URL used in the health probe and included in the message.</param>
    /// <param name="preflight">The preflight health probe result used to determine busy state and reason.</param>
    /// <returns>
    /// A string describing the deferral reason: either that active local requests are being served, that the host reported busy (including the probe's busy reason when present), or that the host did not respond within the stabilization window.
    /// </returns>
    private string BuildBusyHostDeferMessage(string serviceUrl, ContainerHealthStatus preflight)
    {
        if (HasActiveLocalRequests())
        {
            return
                $"Managed local GPU host is serving active requests at {serviceUrl}; deferring restart until those requests complete.";
        }

        if (HasHostReportedActiveWork(preflight))
        {
            var busyReason = string.IsNullOrWhiteSpace(preflight.BusyReason)
                ? BuildHostReportedBusyReason(preflight)
                : preflight.BusyReason;
            return
                $"Managed local GPU host is busy at {serviceUrl}; deferring restart while {busyReason}.";
        }

        return
            $"Managed local GPU host is running at {serviceUrl} but did not answer the health probe within the stabilization window; deferring restart.";
    }

    private static string BuildHostReportedBusyReason(ContainerHealthStatus preflight)
    {
        if (preflight.ActiveRequests > 0)
            return $"{preflight.ActiveRequests} request(s) are active";

        if (preflight.ActiveQwenRequests > 0)
            return $"qwen has {preflight.ActiveQwenRequests} active request(s)";

        if (preflight.ActiveDiarizationRequests > 0)
            return $"diarization has {preflight.ActiveDiarizationRequests} active request(s)";

        if (preflight.ProviderHealth is not null)
        {
            foreach (var snapshot in preflight.ProviderHealth.Values)
            {
                if (snapshot is not null && IsProviderWarmupInProgress(snapshot))
                    return snapshot.Detail ?? "provider warmup is in progress";
            }
        }

        return "the host reported active work";
    }

    /// <summary>
    /// Builds a user-facing failure message describing why the managed local GPU host did not become ready.
    /// </summary>
    /// <param name="readiness">Probe result used to determine the error detail or state to include in the message.</param>
    /// <returns>A single-line message explaining the readiness failure for the managed local GPU host; includes the probe's error detail or state and appends the host process exit code when the tracked process exited before readiness.</returns>
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

            // Check if dependencies changed (would need full venv rebuild)
            var depsMarkerPath = Path.Combine(runtimeRoot, ".bootstrap-version");
            if (!File.Exists(depsMarkerPath))
                return true;
            var storedDepsHash = File.ReadAllText(depsMarkerPath).Trim();
            var currentDepsHash = ComputeBootstrapVersion(
                _requirementsPathResolver(),
                _constraintsPathResolver());
            if (!string.Equals(storedDepsHash, currentDepsHash, StringComparison.Ordinal))
                return true;

            // Check if inference script changed (needs process restart only)
            var scriptMarkerPath = Path.Combine(runtimeRoot, ".script-version");
            if (!File.Exists(scriptMarkerPath))
                return true;
            var storedScriptHash = File.ReadAllText(scriptMarkerPath).Trim();
            var currentScriptHash = ComputeScriptVersion(_inferenceScriptResolver());
            return !string.Equals(storedScriptHash, currentScriptHash, StringComparison.Ordinal);
        }
        catch
        {
            return false; // can't determine — assume unchanged to avoid spurious restarts
        }
    }

    private static string ComputeBootstrapVersion(
        string requirementsPath,
        string constraintsPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine(PythonVersion);
        builder.AppendLine(File.ReadAllText(requirementsPath));
        builder.AppendLine(File.ReadAllText(constraintsPath));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes);
    }

    private static string ComputeScriptVersion(string inferenceScriptPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(inferenceScriptPath)));
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
            health.Capabilities,
            health.CapabilitiesError,
            ProviderHealth: health.ProviderHealth,
            ActiveRequests: health.ActiveRequests,
            ActiveQwenRequests: health.ActiveQwenRequests,
            ActiveDiarizationRequests: health.ActiveDiarizationRequests,
            Busy: health.Busy,
            BusyReason: health.BusyReason,
            QwenMaxConcurrency: health.QwenMaxConcurrency,
            QwenQueueDepth: health.QwenQueueDepth,
            QwenLastQueueWaitMs: health.QwenLastQueueWaitMs,
            QwenLastGenerationMs: health.QwenLastGenerationMs,
            QwenLastReferencePrepMs: health.QwenLastReferencePrepMs,
            QwenLastWarmupMs: health.QwenLastWarmupMs);

    private static bool RequiresCuda(string computeType) =>
        string.Equals(computeType, "float16", StringComparison.OrdinalIgnoreCase)
        || string.Equals(computeType, "float8", StringComparison.OrdinalIgnoreCase);
}
