using Babel.Player.Services;
using Babel.Player.Services.Settings;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for the computed properties of <see cref="BootstrapDiagnostics"/>.
/// These test record construction and pure computed properties without calling Run(),
/// which would require external process probes.
/// </summary>
public sealed class BootstrapDiagnosticsTests
{
    private static BootstrapDiagnostics Make(
        bool python = true,
        string? pythonPath = "/usr/bin/python3",
        bool ffmpeg = true,
        string? ffmpegPath = "/usr/bin/ffmpeg",
        bool piper = false,
        string? piperPath = null,
        bool container = false,
        bool cudaAvailable = false,
        string? cudaVersion = null,
        string? containerUrl = null,
        string cpuVectorLine = "AVX2") =>
        new(
            PythonAvailable: python,
            PythonPath: pythonPath,
            FfmpegAvailable: ffmpeg,
            FfmpegPath: ffmpegPath,
            PiperAvailable: piper,
            PiperPath: piperPath,
            ContainerizedServiceAvailable: container,
            ContainerizedCudaAvailable: cudaAvailable,
            ContainerizedCudaVersion: cudaVersion,
            ContainerizedServiceUrl: containerUrl,
            CpuVectorLine: cpuVectorLine);

    // ── AllDependenciesAvailable ──────────────────────────────────────────────

    [Fact]
    public void AllDependenciesAvailable_BothPresent_ReturnsTrue()
    {
        var diag = Make(python: true, ffmpeg: true);
        Assert.True(diag.AllDependenciesAvailable);
    }

    [Fact]
    public void AllDependenciesAvailable_PythonMissing_ReturnsFalse()
    {
        var diag = Make(python: false, ffmpeg: true);
        Assert.False(diag.AllDependenciesAvailable);
    }

    [Fact]
    public void AllDependenciesAvailable_FfmpegMissing_ReturnsFalse()
    {
        var diag = Make(python: true, ffmpeg: false);
        Assert.False(diag.AllDependenciesAvailable);
    }

    [Fact]
    public void AllDependenciesAvailable_BothMissing_ReturnsFalse()
    {
        var diag = Make(python: false, ffmpeg: false);
        Assert.False(diag.AllDependenciesAvailable);
    }

    [Fact]
    public void AllDependenciesAvailable_PiperMissing_StillTrue()
    {
        // Piper is optional; missing it should NOT affect AllDependenciesAvailable.
        var diag = Make(python: true, ffmpeg: true, piper: false);
        Assert.True(diag.AllDependenciesAvailable);
    }

    // ── DiagnosticSummary ─────────────────────────────────────────────────────

    [Fact]
    public void DiagnosticSummary_AllPresent_ReturnsAllAvailableMessage()
    {
        var diag = Make(python: true, ffmpeg: true);
        Assert.Equal("All dependencies available.", diag.DiagnosticSummary);
    }

    [Fact]
    public void DiagnosticSummary_PythonMissing_MentionsPython()
    {
        var diag = Make(python: false, ffmpeg: true);
        Assert.Contains("Python", diag.DiagnosticSummary);
    }

    [Fact]
    public void DiagnosticSummary_FfmpegMissing_MentionsFfmpeg()
    {
        var diag = Make(python: true, ffmpeg: false);
        Assert.Contains("ffmpeg", diag.DiagnosticSummary);
    }

    [Fact]
    public void DiagnosticSummary_BothMissing_MentionsBoth()
    {
        var diag = Make(python: false, ffmpeg: false);
        Assert.Contains("Python", diag.DiagnosticSummary);
        Assert.Contains("ffmpeg", diag.DiagnosticSummary);
    }

    // ── InferenceLine — no container ─────────────────────────────────────────

    [Fact]
    public void InferenceLine_NoContainer_ReturnsCpuLocal()
    {
        var diag = Make(container: false);
        Assert.Equal("CPU (Local subprocess)", diag.InferenceLine);
    }

    // ── InferenceLine — Docker container with CUDA ───────────────────────────

    [Fact]
    public void InferenceLine_DockerContainerWithCuda_MentionsGpuDocker()
    {
        var diag = Make(
            container: true,
            cudaAvailable: true,
            cudaVersion: "12.1",
            containerUrl: "http://docker-host:8000");

        Assert.Contains("GPU", diag.InferenceLine);
        Assert.Contains("Docker", diag.InferenceLine);
        Assert.Contains("CUDA 12.1", diag.InferenceLine);
    }

    [Fact]
    public void InferenceLine_DockerContainerWithoutCuda_MentionsCpuOnly()
    {
        var diag = Make(
            container: true,
            cudaAvailable: false,
            containerUrl: "http://docker-host:8000");

        Assert.Contains("CPU-only", diag.InferenceLine);
    }

    // ── InferenceLine — managed local host ───────────────────────────────────

    [Fact]
    public void InferenceLine_ManagedLocalHostWithCuda_MentionsManagedLocal()
    {
        var diag = Make(
            container: true,
            cudaAvailable: true,
            cudaVersion: "11.8",
            containerUrl: AppSettings.ManagedGpuServiceUrl);

        Assert.Contains("Managed local", diag.InferenceLine);
        Assert.Contains("CUDA 11.8", diag.InferenceLine);
    }

    [Fact]
    public void InferenceLine_ManagedLocalHostWithoutCuda_MentionsManagedLocalCpuOnly()
    {
        var diag = Make(
            container: true,
            cudaAvailable: false,
            containerUrl: AppSettings.ManagedGpuServiceUrl);

        Assert.Contains("Managed local", diag.InferenceLine);
        Assert.Contains("CPU-only", diag.InferenceLine);
    }

    // ── Record equality ───────────────────────────────────────────────────────

    [Fact]
    public void TwoIdenticalDiagnostics_AreEqual()
    {
        var a = Make(python: true, ffmpeg: true);
        var b = Make(python: true, ffmpeg: true);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentDiagnostics_AreNotEqual()
    {
        var a = Make(python: true, ffmpeg: true);
        var b = Make(python: false, ffmpeg: true);
        Assert.NotEqual(a, b);
    }
}
