using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

public sealed class ManagedVenvHostManagerTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;

    public ManagedVenvHostManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-managed-host-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "managed-host.log"));
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public void ResolveLaunchComputeType_GpuWithCuda_UsesFloat16()
    {
        var computeType = ManagedHostComputeTypePolicy.ResolveLaunchComputeType(
            CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            ComputeProfile.Gpu);

        Assert.Equal("float16", computeType);
    }

    [Fact]
    public void ResolveLaunchComputeType_CpuWithoutAvx2_UsesInt8()
    {
        var computeType = ManagedHostComputeTypePolicy.ResolveLaunchComputeType(
            CreateHardwareSnapshot(hasCuda: false, hasAvx2: false),
            ComputeProfile.Cpu);

        Assert.Equal("int8", computeType);
    }

    [Fact]
    public void CreateHostProcessStartInfo_IncludesComputeTypeArgument()
    {
        var psi = ManagedVenvHostManager.CreateHostProcessStartInfo(
            "python.exe",
            Path.Combine(_dir, "main.py"),
            "float16");

        var args = psi.ArgumentList.ToArray();
        Assert.Contains("--compute-type", args);
        var index = Array.IndexOf(args, "--compute-type");
        Assert.True(index >= 0 && index < args.Length - 1);
        Assert.Equal("float16", args[index + 1]);
        Assert.Contains("--require-cuda", args);
    }

    [Fact]
    public void CreateHostProcessStartInfo_OmitsRequireCudaForInt8()
    {
        var psi = ManagedVenvHostManager.CreateHostProcessStartInfo(
            "python.exe",
            Path.Combine(_dir, "main.py"),
            "int8");

        var args = psi.ArgumentList.ToArray();
        Assert.Contains("--compute-type", args);
        var index = Array.IndexOf(args, "--compute-type");
        Assert.True(index >= 0 && index < args.Length - 1);
        Assert.Equal("int8", args[index + 1]);
        Assert.DoesNotContain("--require-cuda", args);
    }

    [Fact]
    public void CreateHostProcessStartInfo_IncludesRequireCudaForFloat8()
    {
        var psi = ManagedVenvHostManager.CreateHostProcessStartInfo(
            "python.exe",
            Path.Combine(_dir, "main.py"),
            "float8");

        var args = psi.ArgumentList.ToArray();
        Assert.Contains("--compute-type", args);
        var index = Array.IndexOf(args, "--compute-type");
        Assert.True(index >= 0 && index < args.Length - 1);
        Assert.Equal("float8", args[index + 1]);
        Assert.Contains("--require-cuda", args);
    }

    [Fact]
    public async Task EnsureStartedAsync_GpuSelectedWithoutCuda_FailsWithoutFallingBack()
    {
        var manager = new ManagedVenvHostManager(
            _log,
            healthCheckFunc: AlwaysUnavailableHealthCheck(),
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: false, hasAvx2: false));

        var settings = new AppSettings
        {
            TranscriptionProfile = ComputeProfile.Gpu,
            PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
        };

        var result = await manager.EnsureStartedAsync(settings, ContainerizedStartupTrigger.Execution);

        Assert.True(result.Attempted);
        Assert.False(result.IsReady);
        Assert.Contains("CUDA-capable NVIDIA GPU", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureStartedAsync_RuntimeValidatorFailure_ReturnsExplicitManagedRuntimeMessage()
    {
        var manager = new ManagedVenvHostManager(
            _log,
            healthCheckFunc: AlwaysUnavailableHealthCheck(),
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            uvResolver: () => Path.Combine(_dir, "uv.exe"),
            runtimeRootResolver: () => _dir,
            inferenceScriptResolver: () => Path.Combine(_dir, "main.py"),
            requirementsPathResolver: () => Path.Combine(_dir, "gpu-requirements.txt"),
            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>
                Task.CompletedTask,
            runtimeValidator: (_, _) => Task.FromResult(new ManagedGpuRuntimeValidationResult(
                false,
                "PyTorch is installed, but torch.cuda.is_available() returned false.")));

        PrepareBootstrappedRuntimeArtifacts();

        var result = await manager.EnsureStartedAsync(
            new AppSettings
            {
                TranslationProfile = ComputeProfile.Gpu,
                PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
            },
            ContainerizedStartupTrigger.Execution);

        Assert.True(result.Attempted);
        Assert.False(result.IsReady);
        Assert.Contains("torch.cuda.is_available()", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureStartedAsync_RuntimeValidatorSuccess_StartsHostWithFloat16()
    {
        string? startedComputeType = null;
        var healthCheckCount = 0;

        var manager = new ManagedVenvHostManager(
            _log,
            healthCheckFunc: (serviceUrl, _, _) =>
            {
                healthCheckCount++;
                var health = healthCheckCount == 1
                    ? ContainerHealthStatus.Unavailable(serviceUrl, "offline")
                    : new ContainerHealthStatus(
                        true,
                        true,
                        "12.8",
                        serviceUrl,
                        null);
                return Task.FromResult(health);
            },
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            uvResolver: () => Path.Combine(_dir, "uv.exe"),
            runtimeRootResolver: () => _dir,
            inferenceScriptResolver: () => Path.Combine(_dir, "main.py"),
            requirementsPathResolver: () => Path.Combine(_dir, "gpu-requirements.txt"),
            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>
                Task.CompletedTask,
            runtimeValidator: (_, _) => Task.FromResult(new ManagedGpuRuntimeValidationResult(
                true,
                "Managed Python runtime can access CUDA 12.8.",
                "12.8")),
            hostProcessStarter: (_, _, computeType, hostPidPath, token) =>
            {
                startedComputeType = computeType;
                return File.WriteAllTextAsync(hostPidPath, "12345", token);
            });

        PrepareBootstrappedRuntimeArtifacts();

        var result = await manager.EnsureStartedAsync(
            new AppSettings
            {
                TtsProfile = ComputeProfile.Gpu,
                PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
            },
            ContainerizedStartupTrigger.Execution);

        Assert.True(result.Attempted);
        Assert.True(result.IsReady);
        Assert.Equal("float16", startedComputeType);
    }

    [Fact]
    public async Task EnsureStartedAsync_PostStartProbeRetriesUntilHostBecomesAvailable()
    {
        var probeCalls = 0;
        var probe = new ContainerizedServiceProbe(_log, (url, _, _) =>
        {
            var currentCall = Interlocked.Increment(ref probeCalls);
            var health = currentCall < 3
                ? ContainerHealthStatus.Unavailable(url, $"connection refused #{currentCall}")
                : new ContainerHealthStatus(
                    true,
                    true,
                    "12.8",
                    url,
                    null,
                    new ContainerCapabilitiesSnapshot(
                        TranscriptionReady: true,
                        TranscriptionDetail: null,
                        TranslationReady: false,
                        TranslationDetail: "Capabilities probe is still warming or failed: timeout",
                        TtsReady: false,
                        TtsDetail: "Capabilities probe is still warming or failed: timeout"));
            return Task.FromResult(health);
        });

        var manager = new ManagedVenvHostManager(
            _log,
            probe: probe,
            healthCheckFunc: AlwaysUnavailableHealthCheck(),
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            uvResolver: () => Path.Combine(_dir, "uv.exe"),
            runtimeRootResolver: () => _dir,
            inferenceScriptResolver: () => Path.Combine(_dir, "main.py"),
            requirementsPathResolver: () => Path.Combine(_dir, "gpu-requirements.txt"),
            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>
                Task.CompletedTask,
            runtimeValidator: (_, _) => Task.FromResult(new ManagedGpuRuntimeValidationResult(
                true,
                "Managed Python runtime can access CUDA 12.8.",
                "12.8")),
            hostProcessStarter: (_, _, _, hostPidPath, token) =>
                File.WriteAllTextAsync(hostPidPath, "12345", token));

        PrepareBootstrappedRuntimeArtifacts();

        var result = await manager.EnsureStartedAsync(
            new AppSettings
            {
                TranslationProfile = ComputeProfile.Gpu,
                PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
            },
            ContainerizedStartupTrigger.Execution);

        Assert.True(result.Attempted);
        Assert.True(result.IsReady);
        Assert.True(probeCalls >= 3);
    }

    [Fact]
    public async Task EnsureStartedAsync_PostStartProbeFailureReturnsLastUnavailableDetail()
    {
        var probeCalls = 0;
        var probe = new ContainerizedServiceProbe(_log, (url, _, _) =>
        {
            var currentCall = Interlocked.Increment(ref probeCalls);
            return Task.FromResult(ContainerHealthStatus.Unavailable(url, $"connection refused #{currentCall}"));
        });

        var manager = new ManagedVenvHostManager(
            _log,
            probe: probe,
            healthCheckFunc: AlwaysUnavailableHealthCheck(),
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            uvResolver: () => Path.Combine(_dir, "uv.exe"),
            runtimeRootResolver: () => _dir,
            inferenceScriptResolver: () => Path.Combine(_dir, "main.py"),
            requirementsPathResolver: () => Path.Combine(_dir, "gpu-requirements.txt"),
            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>
                Task.CompletedTask,
            runtimeValidator: (_, _) => Task.FromResult(new ManagedGpuRuntimeValidationResult(
                true,
                "Managed Python runtime can access CUDA 12.8.",
                "12.8")),
            hostProcessStarter: (_, _, _, hostPidPath, token) =>
                File.WriteAllTextAsync(hostPidPath, "12345", token));

        PrepareBootstrappedRuntimeArtifacts();

        var result = await manager.EnsureStartedAsync(
            new AppSettings
            {
                TtsProfile = ComputeProfile.Gpu,
                PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
            },
            ContainerizedStartupTrigger.Execution);

        Assert.True(result.Attempted);
        Assert.False(result.IsReady);
        Assert.Contains("connection refused", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(probeCalls >= 2);
    }

    [Fact]
    public async Task EnsureStartedAsync_StaleExistingVenv_RebuildsRuntimeInsteadOfFailing()
    {
        var bootstrapCalls = 0;
        string[]? bootstrapArgs = null;
        var healthCheckCount = 0;

        var manager = new ManagedVenvHostManager(
            _log,
            healthCheckFunc: (serviceUrl, _, _) =>
            {
                healthCheckCount++;
                var health = healthCheckCount == 1
                    ? ContainerHealthStatus.Unavailable(serviceUrl, "offline")
                    : new ContainerHealthStatus(true, true, "12.8", serviceUrl, null);
                return Task.FromResult(health);
            },
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            uvResolver: () => Path.Combine(_dir, "uv.exe"),
            runtimeRootResolver: () => _dir,
            inferenceScriptResolver: () => Path.Combine(_dir, "main.py"),
            requirementsPathResolver: () => Path.Combine(_dir, "gpu-requirements.txt"),
            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>
            {
                bootstrapCalls++;
                bootstrapArgs = new[] { uvPath, venvDir, pythonPath, requirementsPath, constraintsPath };
                Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
                return File.WriteAllTextAsync(pythonPath, string.Empty, token);
            },
            runtimeValidator: (_, _) => Task.FromResult(new ManagedGpuRuntimeValidationResult(
                true,
                "Managed Python runtime can access CUDA 12.8.",
                "12.8")),
            hostProcessStarter: (_, _, _, hostPidPath, token) =>
                File.WriteAllTextAsync(hostPidPath, "12345", token));

        PrepareRuntimeArtifacts(writeBootstrapMarker: false);

        var result = await manager.EnsureStartedAsync(
            new AppSettings
            {
                TranslationProfile = ComputeProfile.Gpu,
                PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
            },
            ContainerizedStartupTrigger.Execution);

        Assert.True(result.Attempted);
        Assert.True(result.IsReady);
        Assert.Equal(1, bootstrapCalls);
        Assert.NotNull(bootstrapArgs);
        Assert.Equal(Path.Combine(_dir, ".venv"), bootstrapArgs![1]);
        Assert.True(File.Exists(Path.Combine(_dir, ".bootstrap-version")));
    }

    [Fact]
    public async Task EnsureStartedAsync_MatchingBootstrapVersion_SkipsBootstrap()
    {
        var bootstrapCalls = 0;
        var healthCheckCount = 0;

        var manager = new ManagedVenvHostManager(
            _log,
            healthCheckFunc: (serviceUrl, _, _) =>
            {
                healthCheckCount++;
                var health = healthCheckCount == 1
                    ? ContainerHealthStatus.Unavailable(serviceUrl, "offline")
                    : new ContainerHealthStatus(true, true, "12.8", serviceUrl, null);
                return Task.FromResult(health);
            },
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            uvResolver: () => Path.Combine(_dir, "uv.exe"),
            runtimeRootResolver: () => _dir,
            inferenceScriptResolver: () => Path.Combine(_dir, "main.py"),
            requirementsPathResolver: () => Path.Combine(_dir, "gpu-requirements.txt"),
            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            bootstrapRunner: (_, _, _, _, _, _) =>
            {
                bootstrapCalls++;
                return Task.CompletedTask;
            },
            runtimeValidator: (_, _) => Task.FromResult(new ManagedGpuRuntimeValidationResult(
                true,
                "Managed Python runtime can access CUDA 12.8.",
                "12.8")),
            hostProcessStarter: (_, _, _, hostPidPath, token) =>
                File.WriteAllTextAsync(hostPidPath, "12345", token));

        PrepareBootstrappedRuntimeArtifacts();

        var result = await manager.EnsureStartedAsync(
            new AppSettings
            {
                TtsProfile = ComputeProfile.Gpu,
                PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
            },
            ContainerizedStartupTrigger.Execution);

        Assert.True(result.Attempted);
        Assert.True(result.IsReady);
        Assert.Equal(0, bootstrapCalls);
    }

    [Fact]
    public async Task EnsureStartedAsync_TrackedStaleHostWithBootstrap_KillsProcessAndRebuilds()
    {
        var bootstrapCalls = 0;
        var healthCheckCount = 0;

        using var staleProcess = StartLongRunningProcess();
        var manager = new ManagedVenvHostManager(
            _log,
            healthCheckFunc: (serviceUrl, _, _) =>
            {
                healthCheckCount++;
                var health = healthCheckCount == 1
                    ? ContainerHealthStatus.Unavailable(serviceUrl, "offline")
                    : new ContainerHealthStatus(true, true, "12.8", serviceUrl, null);
                return Task.FromResult(health);
            },
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            uvResolver: () => Path.Combine(_dir, "uv.exe"),
            runtimeRootResolver: () => _dir,
            inferenceScriptResolver: () => Path.Combine(_dir, "main.py"),
            requirementsPathResolver: () => Path.Combine(_dir, "gpu-requirements.txt"),
            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            bootstrapRunner: (_, _, pythonPath, _, _, token) =>
            {
                bootstrapCalls++;
                Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
                return File.WriteAllTextAsync(pythonPath, string.Empty, token);
            },
            runtimeValidator: (_, _) => Task.FromResult(new ManagedGpuRuntimeValidationResult(
                true,
                "Managed Python runtime can access CUDA 12.8.",
                "12.8")),
            hostProcessStarter: (_, _, _, hostPidPath, token) =>
                File.WriteAllTextAsync(hostPidPath, "12345", token));

        PrepareRuntimeArtifacts(writeBootstrapMarker: false);
        File.WriteAllText(Path.Combine(_dir, "managed-host.pid"), staleProcess.Id.ToString());
        SetTrackedHostProcess(manager, staleProcess);

        var result = await manager.EnsureStartedAsync(
            new AppSettings
            {
                TranslationProfile = ComputeProfile.Gpu,
                PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
            },
            ContainerizedStartupTrigger.Execution);

        Assert.True(result.Attempted);
        Assert.True(result.IsReady);
        Assert.Equal(1, bootstrapCalls);
        await WaitForExitAsync(staleProcess);
    }

    [Fact]
    public async Task EnsureStartedAsync_HealthUnavailableWithTrackedHost_RestartsInsteadOfReusing()
    {
        var healthCheckCount = 0;
        var hostStartCalls = 0;

        using var staleProcess = StartLongRunningProcess();
        var manager = new ManagedVenvHostManager(
            _log,
            healthCheckFunc: (serviceUrl, _, _) =>
            {
                healthCheckCount++;
                var health = healthCheckCount == 1
                    ? ContainerHealthStatus.Unavailable(serviceUrl, "offline")
                    : new ContainerHealthStatus(true, true, "12.8", serviceUrl, null);
                return Task.FromResult(health);
            },
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            uvResolver: () => Path.Combine(_dir, "uv.exe"),
            runtimeRootResolver: () => _dir,
            inferenceScriptResolver: () => Path.Combine(_dir, "main.py"),
            requirementsPathResolver: () => Path.Combine(_dir, "gpu-requirements.txt"),
            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>
                Task.CompletedTask,
            runtimeValidator: (_, _) => Task.FromResult(new ManagedGpuRuntimeValidationResult(
                true,
                "Managed Python runtime can access CUDA 12.8.",
                "12.8")),
            hostProcessStarter: (_, _, _, hostPidPath, token) =>
            {
                hostStartCalls++;
                return File.WriteAllTextAsync(hostPidPath, "12345", token);
            });

        PrepareBootstrappedRuntimeArtifacts();
        SetTrackedHostProcess(manager, staleProcess);

        var result = await manager.EnsureStartedAsync(
            new AppSettings
            {
                TtsProfile = ComputeProfile.Gpu,
                PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
            },
            ContainerizedStartupTrigger.Execution);

        Assert.True(result.Attempted);
        Assert.True(result.IsReady);
        Assert.Equal(1, hostStartCalls);
        await WaitForExitAsync(staleProcess);
    }

    [Fact]
    public async Task EnsureStartedAsync_SettingsChangedTimeoutWithTrackedHost_DefersRestart()
    {
        var hostStartCalls = 0;

        using var trackedProcess = StartLongRunningProcess();
        var manager = new ManagedVenvHostManager(
            _log,
            healthCheckFunc: (serviceUrl, _, _) =>
                Task.FromResult(ContainerHealthStatus.Unavailable(
                    serviceUrl,
                    "The request was canceled due to the configured HttpClient.Timeout of 1 seconds elapsing.")),
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            uvResolver: () => Path.Combine(_dir, "uv.exe"),
            runtimeRootResolver: () => _dir,
            inferenceScriptResolver: () => Path.Combine(_dir, "main.py"),
            requirementsPathResolver: () => Path.Combine(_dir, "gpu-requirements.txt"),
            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            runtimeValidator: (_, _) => Task.FromResult(new ManagedGpuRuntimeValidationResult(
                true,
                "Managed Python runtime can access CUDA 12.8.",
                "12.8")),
            hostProcessStarter: (_, _, _, hostPidPath, token) =>
            {
                hostStartCalls++;
                return File.WriteAllTextAsync(hostPidPath, "12345", token);
            });

        PrepareBootstrappedRuntimeArtifacts();
        SetTrackedHostProcess(manager, trackedProcess);

        var result = await manager.EnsureStartedAsync(
            new AppSettings
            {
                TranslationProfile = ComputeProfile.Gpu,
                PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
            },
            ContainerizedStartupTrigger.SettingsChanged);

        Assert.False(result.Attempted);
        Assert.True(result.IsReady);
        Assert.Contains("deferring restart", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, hostStartCalls);
        Assert.False(trackedProcess.HasExited);

        trackedProcess.Kill(entireProcessTree: true);
        await WaitForExitAsync(trackedProcess);
    }

    [Fact]
    public async Task EnsureStartedAsync_StalePidFileWithoutLiveProcess_RemovesItAndContinues()
    {
        var healthCheckCount = 0;

        var manager = new ManagedVenvHostManager(
            _log,
            healthCheckFunc: (serviceUrl, _, _) =>
            {
                healthCheckCount++;
                var health = healthCheckCount == 1
                    ? ContainerHealthStatus.Unavailable(serviceUrl, "offline")
                    : new ContainerHealthStatus(true, true, "12.8", serviceUrl, null);
                return Task.FromResult(health);
            },
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            uvResolver: () => Path.Combine(_dir, "uv.exe"),
            runtimeRootResolver: () => _dir,
            inferenceScriptResolver: () => Path.Combine(_dir, "main.py"),
            requirementsPathResolver: () => Path.Combine(_dir, "gpu-requirements.txt"),
            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>
                Task.CompletedTask,
            runtimeValidator: (_, _) => Task.FromResult(new ManagedGpuRuntimeValidationResult(
                true,
                "Managed Python runtime can access CUDA 12.8.",
                "12.8")),
            hostProcessStarter: (_, _, _, hostPidPath, token) =>
                File.WriteAllTextAsync(hostPidPath, "12345", token));

        PrepareBootstrappedRuntimeArtifacts();
        File.WriteAllText(Path.Combine(_dir, "managed-host.pid"), "999999");

        var result = await manager.EnsureStartedAsync(
            new AppSettings
            {
                TranscriptionProfile = ComputeProfile.Gpu,
                PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
            },
            ContainerizedStartupTrigger.Execution);

        Assert.True(result.Attempted);
        Assert.True(result.IsReady);
        Assert.Equal("12345", File.ReadAllText(Path.Combine(_dir, "managed-host.pid")));
    }

    [Fact]
    public async Task Dispose_WithPidTrackedManagedPythonProcess_KillsProcessAndRemovesPidFile()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var runtimeRoot = _dir;
        var pythonPath = Path.Combine(runtimeRoot, ".venv", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);

        var systemCmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        File.Copy(systemCmdPath, pythonPath, overwrite: true);

        var pidPath = Path.Combine(runtimeRoot, "managed-host.pid");

        var psi = new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("ping -n 30 127.0.0.1");

        using var staleProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start fake managed python process.");
        File.WriteAllText(pidPath, staleProcess.Id.ToString());

        var manager = new ManagedVenvHostManager(
            _log,
            runtimeRootResolver: () => runtimeRoot);

        manager.Dispose();

        await WaitForExitAsync(staleProcess);

        Assert.True(staleProcess.HasExited);
        Assert.False(File.Exists(pidPath));
    }

    [Fact]
    public async Task EnsureStartedAsync_BootstrapFailure_ReturnsFailedResultWithProcessDetail()
    {
        var manager = new ManagedVenvHostManager(
            _log,
            healthCheckFunc: AlwaysUnavailableHealthCheck(),
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            uvResolver: () => Path.Combine(_dir, "uv.exe"),
            runtimeRootResolver: () => _dir,
            inferenceScriptResolver: () => Path.Combine(_dir, "main.py"),
            requirementsPathResolver: () => Path.Combine(_dir, "gpu-requirements.txt"),
            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            bootstrapRunner: (_, _, _, _, _, _) => throw new InvalidOperationException("Process 'uv venv --clear' failed with exit code 2: already exists"));

        PrepareRuntimeArtifacts(writeBootstrapMarker: false);

        var result = await manager.EnsureStartedAsync(
            new AppSettings
            {
                TranscriptionProfile = ComputeProfile.Gpu,
                PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
            },
            ContainerizedStartupTrigger.Execution);

        Assert.True(result.Attempted);
        Assert.False(result.IsReady);
        Assert.Contains("runtime bootstrap failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exit code 2", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ManagedHostState.Failed, manager.State);
        Assert.Equal(result.Message, manager.FailureReason);
    }

    [Fact]
    public async Task EnsureStartedAsync_LockedRuntimeBootstrapFailure_ReturnsExplicitLockedRuntimeMessage()
    {
        var manager = new ManagedVenvHostManager(
            _log,
            healthCheckFunc: AlwaysUnavailableHealthCheck(),
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            uvResolver: () => Path.Combine(_dir, "uv.exe"),
            runtimeRootResolver: () => _dir,
            inferenceScriptResolver: () => Path.Combine(_dir, "main.py"),
            requirementsPathResolver: () => Path.Combine(_dir, "gpu-requirements.txt"),
            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            bootstrapRunner: (_, _, _, _, _, _) => throw new InvalidOperationException(
                "Process 'uv venv --clear' failed with exit code 2: failed to remove directory '\\\\?\\C:\\test\\.venv\\Scripts': Access is denied. (os error 5)"));

        PrepareRuntimeArtifacts(writeBootstrapMarker: false);

        var result = await manager.EnsureStartedAsync(
            new AppSettings
            {
                TranslationProfile = ComputeProfile.Gpu,
                PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
            },
            ContainerizedStartupTrigger.Execution);

        Assert.True(result.Attempted);
        Assert.False(result.IsReady);
        Assert.Contains("locked by a stale host process", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".venv", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestEnsureStarted_BootstrapFailure_LogsFailureAndSetsState()
    {
        var manager = new ManagedVenvHostManager(
            _log,
            healthCheckFunc: AlwaysUnavailableHealthCheck(),
            hardwareSnapshotProvider: () => CreateHardwareSnapshot(hasCuda: true, hasAvx2: true),
            uvResolver: () => Path.Combine(_dir, "uv.exe"),
            runtimeRootResolver: () => _dir,
            inferenceScriptResolver: () => Path.Combine(_dir, "main.py"),
            requirementsPathResolver: () => Path.Combine(_dir, "gpu-requirements.txt"),
            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            bootstrapRunner: (_, _, _, _, _, _) => throw new InvalidOperationException("Process 'uv venv --clear' failed with exit code 2: stale venv"));

        PrepareRuntimeArtifacts(writeBootstrapMarker: false);

        manager.RequestEnsureStarted(
            new AppSettings
            {
                AlwaysStartLocalGpuRuntimeAtAppStart = true,
                PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
            },
            ContainerizedStartupTrigger.AppStartup);

        await WaitForAsync(() => manager.State == ManagedHostState.Failed);
        var logContents = await ReadLogAsync();

        Assert.Contains("runtime bootstrap failed", manager.FailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runtime bootstrap failed", logContents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unobserved task exception", logContents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PythonVersion_IsThreePointTwelve()
    {
        var field = typeof(ManagedVenvHostManager).GetField(
            "PythonVersion",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = field!.GetRawConstantValue() as string;
        Assert.Equal("3.12", value);
    }

    [Fact]
    public void PythonVersion_IsNotOldPatchVersion_Regression()
    {
        // Regression: PythonVersion was previously "3.11.6"; it is now the unpinned major.minor "3.12"
        var field = typeof(ManagedVenvHostManager).GetField(
            "PythonVersion",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = field!.GetRawConstantValue() as string;
        Assert.NotEqual("3.11.6", value);
    }

    [Fact]
    public void PythonVersion_IsMajorMinorFormatWithoutPatch()
    {
        // The version string should be in "major.minor" format (e.g. "3.12"), not "major.minor.patch" (e.g. "3.11.6")
        // uv resolves to the latest available patch for the given major.minor, so specifying a patch version here is fragile.
        var field = typeof(ManagedVenvHostManager).GetField(
            "PythonVersion",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = field!.GetRawConstantValue() as string;
        Assert.NotNull(value);
        var parts = value!.Split('.');
        Assert.Equal(2, parts.Length);
        Assert.True(int.TryParse(parts[0], out var major) && major >= 3, $"Expected major version >= 3, got '{parts[0]}'");
        Assert.True(int.TryParse(parts[1], out var minor) && minor >= 0, $"Expected valid minor version, got '{parts[1]}'");
    }

    private static HardwareSnapshot CreateHardwareSnapshot(bool hasCuda, bool hasAvx2) =>
        new(
            IsDetecting: false,
            CpuName: "Test CPU",
            CpuCores: 8,
            HasAvx: hasAvx2,
            HasAvx2: hasAvx2,
            HasAvx512F: false,
            SystemRamGb: 32,
            GpuName: hasCuda ? "NVIDIA Test GPU" : null,
            GpuVramMb: hasCuda ? 8192 : null,
            HasCuda: hasCuda,
            CudaVersion: hasCuda ? "12.8" : null,
            HasOpenVino: false,
            OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: hasCuda,
            IsVsrDriverSufficient: hasCuda,
            NvidiaDriverVersion: hasCuda ? "552.00" : null);

    private static Func<string, TimeSpan, CancellationToken, Task<ContainerHealthStatus>> AlwaysUnavailableHealthCheck(
        string errorMessage = "offline") =>
        (serviceUrl, _, _) => Task.FromResult(ContainerHealthStatus.Unavailable(serviceUrl, errorMessage));

    private static string ComputeBootstrapVersion(string requirementsPath, string constraintsPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("3.12"); // PythonVersion constant from ManagedVenvHostManager
        builder.AppendLine(File.ReadAllText(requirementsPath));
        builder.AppendLine(File.ReadAllText(constraintsPath));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string ComputeScriptVersion(string inferenceScriptPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(inferenceScriptPath))));

    private void PrepareBootstrappedRuntimeArtifacts() =>
        PrepareRuntimeArtifacts(writeBootstrapMarker: true);

    private void PrepareRuntimeArtifacts(bool writeBootstrapMarker)
    {
        File.WriteAllText(Path.Combine(_dir, "uv.exe"), "");
        var scriptPath = Path.Combine(_dir, "main.py");
        var requirementsPath = Path.Combine(_dir, "gpu-requirements.txt");
        var constraintsPath = Path.Combine(_dir, "gpu-constraints.txt");
        File.WriteAllText(scriptPath, "print('test')");
        File.WriteAllText(requirementsPath, "torch==2.8.0");
        File.WriteAllText(constraintsPath, "torch==2.8.0");
        Directory.CreateDirectory(Path.Combine(_dir, ".venv", "Scripts"));
        File.WriteAllText(Path.Combine(_dir, ".venv", "Scripts", "python.exe"), "");
        if (writeBootstrapMarker)
        {
            File.WriteAllText(
                Path.Combine(_dir, ".bootstrap-version"),
                ComputeBootstrapVersion(requirementsPath, constraintsPath));
            File.WriteAllText(
                Path.Combine(_dir, ".script-version"),
                ComputeScriptVersion(scriptPath));
        }
        else
        {
            var markerPath = Path.Combine(_dir, ".bootstrap-version");
            if (File.Exists(markerPath))
                File.Delete(markerPath);
            var scriptMarkerPath = Path.Combine(_dir, ".script-version");
            if (File.Exists(scriptMarkerPath))
                File.Delete(scriptMarkerPath);
        }
    }

    private async Task<string> ReadLogAsync()
    {
        await _log.FlushAsync();
        return File.Exists(_log.LogFilePath)
            ? await File.ReadAllTextAsync(_log.LogFilePath)
            : string.Empty;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, cts.Token);
        }
    }

    private static Process StartLongRunningProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sleep",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("ping -n 30 127.0.0.1");
        }
        else
        {
            psi.ArgumentList.Add("30");
        }

        return Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start test process.");
    }

    private static void SetTrackedHostProcess(ManagedVenvHostManager manager, Process process)
    {
        var field = typeof(ManagedVenvHostManager).GetField("_hostProcess", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find _hostProcess field.");
        field.SetValue(manager, process);
    }

    private static async Task WaitForExitAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync();
        }
        catch
        {
        }
    }
}
