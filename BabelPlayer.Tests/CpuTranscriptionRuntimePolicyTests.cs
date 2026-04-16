using Babel.Player.Services;
using Babel.Player.Services.Settings;
using Babel.Player.Services.Transcription;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class CpuTranscriptionRuntimePolicyTests
{
    private static HardwareSnapshot MakeHw(
        int logical,
        int? physical,
        bool avx512,
        double ramGb) =>
        new(
            IsDetecting: false,
            CpuName: "Test",
            CpuCores: logical,
            HasAvx: true,
            HasAvx2: true,
            HasAvx512F: avx512,
            SystemRamGb: ramGb,
            GpuName: null,
            GpuVramMb: null,
            HasCuda: false,
            CudaVersion: null,
            HasOpenVino: false,
            OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: false,
            IsVsrDriverSufficient: false,
            NvidiaDriverVersion: null,
            GpuComputeCapability: null,
            CpuPhysicalCores: physical);

    [Fact]
    public void AutoCompute_WithoutAvx512_UsesInt8()
    {
        var hw = MakeHw(16, 8, avx512: false, ramGb: 64);
        var s = CpuTranscriptionRuntimePolicy.ResolveEffectiveComputeType("auto", hw);
        Assert.Equal("int8", s);
    }

    [Fact]
    public void AutoCompute_WithAvx512_UsesInt8Float16()
    {
        var hw = MakeHw(16, 8, avx512: true, ramGb: 64);
        var s = CpuTranscriptionRuntimePolicy.ResolveEffectiveComputeType("auto", hw);
        Assert.Equal("int8_float16", s);
    }

    [Fact]
    public void AutoWorkers_UsesMinOfCoreAndRamBudgets()
    {
        var hw = MakeHw(16, 8, avx512: false, ramGb: 64);
        // Core budget: 8 - 1 = 7; RAM: floor((64-4)/2) = 30 → min = 7
        Assert.Equal(7, CpuTranscriptionRuntimePolicy.ComputeAutoNumWorkers(hw));
    }

    [Fact]
    public void ResolveForLocalCpu_RespectsManualWorkersClamp()
    {
        var hw = MakeHw(8, 4, avx512: false, ramGb: 64);
        var settings = new AppSettings
        {
            TranscriptionCpuComputeType = "int8",
            TranscriptionCpuThreads = 0,
            TranscriptionNumWorkersUseAuto = false,
            TranscriptionNumWorkers = 999,
        };
        var p = CpuTranscriptionRuntimePolicy.ResolveForLocalCpu(settings, hw);
        Assert.Equal(7, p.NumWorkers);
    }
}
