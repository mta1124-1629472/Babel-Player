using System;
using System.IO;
using System.Linq;
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
            NvidiaDriverVersion: hasCuda ? "552.00" : null,
            IsHdrDisplayAvailable: false);

    private static Func<string, TimeSpan, CancellationToken, Task<ContainerHealthStatus>> AlwaysUnavailableHealthCheck(
        string errorMessage = "offline") =>
        (serviceUrl, _, _) => Task.FromResult(ContainerHealthStatus.Unavailable(serviceUrl, errorMessage));

    private static string ComputeBootstrapVersion(string inferenceScriptPath, string requirementsPath, string constraintsPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine(File.ReadAllText(inferenceScriptPath));
        builder.AppendLine(File.ReadAllText(requirementsPath));
        builder.AppendLine(File.ReadAllText(constraintsPath));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

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
                ComputeBootstrapVersion(scriptPath, requirementsPath, constraintsPath));
        }
        else
        {
            var markerPath = Path.Combine(_dir, ".bootstrap-version");
            if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
    }

    private async Task<string> ReadLogAsync()
    {
        await Task.Delay(150);
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
}
