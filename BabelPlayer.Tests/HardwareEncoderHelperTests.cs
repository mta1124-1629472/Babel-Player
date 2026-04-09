using Babel.Player.Services;
using Babel.Player.Services.Settings;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for <see cref="HardwareEncoderHelper.ResolveEncoder"/>.
/// </summary>
public sealed class HardwareEncoderHelperTests
{
    private static HardwareSnapshot MakeSnapshot(
        bool hasCuda = false,
        string? gpuName = null) =>
        new(
            IsDetecting: false,
            CpuName: "Test CPU",
            CpuCores: 4,
            HasAvx: true,
            HasAvx2: true,
            HasAvx512F: false,
            SystemRamGb: 16,
            GpuName: gpuName,
            GpuVramMb: null,
            HasCuda: hasCuda,
            CudaVersion: hasCuda ? "12.0" : null,
            HasOpenVino: false,
            OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: false,
            IsVsrDriverSufficient: false,
            NvidiaDriverVersion: null);

    // ── Explicit encoder override ─────────────────────────────────────────────

    [Fact]
    public void ResolveEncoder_ExplicitEncoder_ReturnsThatEncoder()
    {
        var settings = new AppSettings { VideoExportEncoder = "hevc_nvenc" };
        var hw = MakeSnapshot();
        Assert.Equal("hevc_nvenc", HardwareEncoderHelper.ResolveEncoder(settings, hw));
    }

    [Fact]
    public void ResolveEncoder_ExplicitLibx264_ReturnsThatEncoder()
    {
        var settings = new AppSettings { VideoExportEncoder = "libx264" };
        var hw = MakeSnapshot(hasCuda: true); // CUDA present but explicit override wins
        Assert.Equal("libx264", HardwareEncoderHelper.ResolveEncoder(settings, hw));
    }

    // ── Auto: CUDA → NVENC ────────────────────────────────────────────────────

    [Fact]
    public void ResolveEncoder_Auto_WithCuda_ReturnsNvenc()
    {
        var settings = new AppSettings { VideoExportEncoder = "auto" };
        var hw = MakeSnapshot(hasCuda: true);
        Assert.Equal("h264_nvenc", HardwareEncoderHelper.ResolveEncoder(settings, hw));
    }

    // ── Auto: AMD GPU ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("AMD Radeon RX 6800")]
    [InlineData("Radeon RX 580")]
    [InlineData("AMD GPU")]
    public void ResolveEncoder_Auto_WithAmdGpu_ReturnsAmf(string gpuName)
    {
        var settings = new AppSettings { VideoExportEncoder = "auto" };
        var hw = MakeSnapshot(hasCuda: false, gpuName: gpuName);
        Assert.Equal("h264_amf", HardwareEncoderHelper.ResolveEncoder(settings, hw));
    }

    // ── Auto: Intel GPU ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Intel UHD Graphics 770")]
    [InlineData("Intel Arc A770")]
    public void ResolveEncoder_Auto_WithIntelGpu_ReturnsQsv(string gpuName)
    {
        var settings = new AppSettings { VideoExportEncoder = "auto" };
        var hw = MakeSnapshot(hasCuda: false, gpuName: gpuName);
        Assert.Equal("h264_qsv", HardwareEncoderHelper.ResolveEncoder(settings, hw));
    }

    // ── Auto: software fallback ───────────────────────────────────────────────

    [Fact]
    public void ResolveEncoder_Auto_NoCuda_NoGpuName_ReturnsLibx264()
    {
        var settings = new AppSettings { VideoExportEncoder = "auto" };
        var hw = MakeSnapshot(hasCuda: false, gpuName: null);
        Assert.Equal("libx264", HardwareEncoderHelper.ResolveEncoder(settings, hw));
    }

    [Fact]
    public void ResolveEncoder_Auto_NoCuda_UnknownGpuName_ReturnsLibx264()
    {
        var settings = new AppSettings { VideoExportEncoder = "auto" };
        var hw = MakeSnapshot(hasCuda: false, gpuName: "Mystery GPU 9000");
        Assert.Equal("libx264", HardwareEncoderHelper.ResolveEncoder(settings, hw));
    }

    [Fact]
    public void ResolveEncoder_Auto_EmptyGpuName_ReturnsLibx264()
    {
        var settings = new AppSettings { VideoExportEncoder = "auto" };
        var hw = MakeSnapshot(hasCuda: false, gpuName: "");
        Assert.Equal("libx264", HardwareEncoderHelper.ResolveEncoder(settings, hw));
    }

    // ── GPU name casing ───────────────────────────────────────────────────────

    [Fact]
    public void ResolveEncoder_Auto_AmdGpuNameUppercase_ReturnsAmf()
    {
        var settings = new AppSettings { VideoExportEncoder = "auto" };
        var hw = MakeSnapshot(hasCuda: false, gpuName: "AMD RADEON RX 7900 XTX");
        Assert.Equal("h264_amf", HardwareEncoderHelper.ResolveEncoder(settings, hw));
    }

    [Fact]
    public void ResolveEncoder_Auto_IntelGpuNameUppercase_ReturnsQsv()
    {
        var settings = new AppSettings { VideoExportEncoder = "auto" };
        var hw = MakeSnapshot(hasCuda: false, gpuName: "INTEL HD GRAPHICS 630");
        Assert.Equal("h264_qsv", HardwareEncoderHelper.ResolveEncoder(settings, hw));
    }
}
