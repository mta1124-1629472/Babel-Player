using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class HardwareSnapshotRtxVideoGateTests
{
    private static HardwareSnapshot Snap(
        bool isDetecting,
        bool isRtxCapable,
        bool isVsrDriverSufficient,
        string? gpuName = "NVIDIA GeForce RTX 3060") =>
        new(
            IsDetecting: isDetecting,
            CpuName: "Test",
            CpuCores: 8,
            HasAvx: true,
            HasAvx2: true,
            HasAvx512F: false,
            SystemRamGb: 16,
            GpuName: gpuName,
            GpuVramMb: 12000,
            HasCuda: true,
            CudaVersion: "12.0",
            HasOpenVino: false,
            OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: isRtxCapable,
            IsVsrDriverSufficient: isVsrDriverSufficient,
            NvidiaDriverVersion: "551.23",
            GpuComputeCapability: null);

    [Fact]
    public void MeetsNvidiaRtxVideoHardwareGate_TrueWhenRtxAndDriverFloor()
    {
        var s = Snap(isDetecting: false, isRtxCapable: true, isVsrDriverSufficient: true);
        Assert.True(s.MeetsNvidiaRtxVideoHardwareGate);
    }

    [Fact]
    public void MeetsNvidiaRtxVideoHardwareGate_FalseWhileDetecting()
    {
        var s = Snap(isDetecting: true, isRtxCapable: true, isVsrDriverSufficient: true);
        Assert.False(s.MeetsNvidiaRtxVideoHardwareGate);
    }

    [Fact]
    public void MeetsNvidiaRtxVideoHardwareGate_FalseWithoutRtxClassGpu()
    {
        var s = Snap(isDetecting: false, isRtxCapable: false, isVsrDriverSufficient: true, gpuName: "NVIDIA GeForce GTX 1080");
        Assert.False(s.MeetsNvidiaRtxVideoHardwareGate);
    }

    [Fact]
    public void MeetsNvidiaRtxVideoHardwareGate_FalseWhenDriverBelowFloor()
    {
        var s = new HardwareSnapshot(
            IsDetecting: false,
            CpuName: "Test",
            CpuCores: 8,
            HasAvx: true,
            HasAvx2: true,
            HasAvx512F: false,
            SystemRamGb: 16,
            GpuName: "NVIDIA GeForce RTX 3060",
            GpuVramMb: 12000,
            HasCuda: true,
            CudaVersion: "12.0",
            HasOpenVino: false,
            OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: true,
            IsVsrDriverSufficient: false,
            NvidiaDriverVersion: "550.00",
            GpuComputeCapability: null);
        Assert.False(s.MeetsNvidiaRtxVideoHardwareGate);
    }
}
