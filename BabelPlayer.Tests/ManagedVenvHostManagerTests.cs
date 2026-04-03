using System;
using System.IO;
using System.Linq;
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
    }

    [Fact]
    public async Task EnsureStartedAsync_GpuSelectedWithoutCuda_FailsWithoutFallingBack()
    {
        var manager = new ManagedVenvHostManager(
            _log,
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
}
