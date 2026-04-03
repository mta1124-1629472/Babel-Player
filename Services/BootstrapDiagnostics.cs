using System.Linq;

namespace Babel.Player.Services;

/// <summary>
/// Captures the result of probing the runtime environment for required external tools
/// and the containerized inference service.
/// Call <see cref="Run"/> once at startup and expose the result as an observable property.
/// </summary>
public sealed record BootstrapDiagnostics(
    bool PythonAvailable,
    string? PythonPath,
    bool FfmpegAvailable,
    string? FfmpegPath,
    bool PiperAvailable,
    string? PiperPath,
    bool ContainerizedServiceAvailable,
    bool ContainerizedCudaAvailable,
    string? ContainerizedCudaVersion,
    string? ContainerizedServiceUrl,
    string CpuVectorLine)
{
    /// <summary>
    /// True only when all hard-required runtime dependencies are present.
    /// <c>PiperAvailable</c> is intentionally excluded — Piper is an optional provider
    /// dependency, not a startup requirement. It is checked at the readiness gate when
    /// the Piper TTS provider is selected.
    /// </summary>
    public bool AllDependenciesAvailable => PythonAvailable && FfmpegAvailable;

    public string DiagnosticSummary => AllDependenciesAvailable
        ? "All dependencies available."
        : string.Join("; ", new[]
        {
            !PythonAvailable ? "Python not found" : null,
            !FfmpegAvailable ? "ffmpeg not found" : null,
        }.Where(s => s is not null));

    /// <summary>
    /// Human-readable inference mode line suitable for the hardware/bootstrap panel.
    /// Shows whether GPU-backed containerized inference is available.
    /// </summary>
    public string InferenceLine
    {
        get
        {
            if (ContainerizedServiceAvailable)
            {
                var cuda = ContainerizedCudaAvailable
                    ? $"CUDA {ContainerizedCudaVersion ?? "\u2713"}"
                    : "CPU-only";
                return $"Containerized ({cuda})";
            }
            return "Local subprocess (CPU)";
        }
    }

    /// <summary>
    /// Probes the local environment and returns a populated <see cref="BootstrapDiagnostics"/>.
    /// Blocking — call from a background thread or accept the brief startup delay (~500 ms worst-case).
    /// </summary>
    /// <param name="containerServiceUrl">
    /// Base URL of the containerized inference service to health-check.
    /// Pass <c>null</c> to skip the container probe (fields default to unavailable/false/null).
    /// </param>
    /// <param name="hardwareSnapshot">
    /// Optional pre-computed <see cref="HardwareSnapshot"/>. When supplied the CPU vector
    /// line is taken from it directly, avoiding a redundant AVX probe. When null a
    /// lightweight AVX-only snapshot is taken inline.
    /// </param>
    public static BootstrapDiagnostics Run(
        string? containerServiceUrl = null,
        HardwareSnapshot? hardwareSnapshot = null)
    {
        var pythonPath = DependencyLocator.FindPython();
        var ffmpegPath = DependencyLocator.FindFfmpeg();
        var piperPath  = DependencyLocator.FindPiper();

        ContainerHealthStatus containerHealth;
        if (!string.IsNullOrWhiteSpace(containerServiceUrl))
            containerHealth = ContainerizedInferenceClient.CheckHealth(containerServiceUrl);
        else
            containerHealth = ContainerHealthStatus.Unavailable(containerServiceUrl ?? "");

        // Prefer the caller-supplied snapshot (already computed at startup).
        // Fall back to a lightweight inline probe if none was supplied.
        var cpuVectorLine = hardwareSnapshot?.CpuVectorLine
            ?? InlineAvxProbe();

        return new BootstrapDiagnostics(
            pythonPath is not null, pythonPath,
            ffmpegPath is not null, ffmpegPath,
            piperPath  is not null, piperPath,
            containerHealth.IsAvailable,
            containerHealth.CudaAvailable,
            containerHealth.CudaVersion,
            containerServiceUrl,
            cpuVectorLine);
    }

    // ── Inline AVX probe (used only when no HardwareSnapshot is supplied) ──────

    /// <summary>
    /// Lightweight AVX tier detection that mirrors <see cref="HardwareSnapshot.CpuVectorLine"/>
    /// without requiring a full hardware snapshot run.
    /// </summary>
    private static string InlineAvxProbe()
    {
        try
        {
            if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported) return "AVX-512F";
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)   return "AVX2";
            if (System.Runtime.Intrinsics.X86.Avx.IsSupported)    return "AVX (no AVX2 \u2014 reduced inference performance)";
        }
        catch { /* fall through */ }
        return "none detected (inference will be significantly slower)";
    }
}
