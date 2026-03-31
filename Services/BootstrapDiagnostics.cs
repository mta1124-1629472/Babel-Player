using System.Linq;

namespace Babel.Player.Services;

/// <summary>
/// Captures the result of probing the runtime environment for required external tools.
/// Call <see cref="Run"/> once at startup and expose the result as an observable property.
/// </summary>
public sealed record BootstrapDiagnostics(
    bool PythonAvailable,
    string? PythonPath,
    bool FfmpegAvailable,
    string? FfmpegPath)
{
    public bool AllDependenciesAvailable => PythonAvailable && FfmpegAvailable;

    public string DiagnosticSummary => AllDependenciesAvailable
        ? "All dependencies available."
        : string.Join("; ", new[]
        {
            !PythonAvailable ? "Python not found" : null,
            !FfmpegAvailable ? "ffmpeg not found" : null,
        }.Where(s => s is not null));

    /// <summary>
    /// Probes the local environment and returns a populated <see cref="BootstrapDiagnostics"/>.
    /// Blocking — call from a background thread or accept the brief startup delay (~500 ms worst-case).
    /// </summary>
    public static BootstrapDiagnostics Run()
    {
        var pythonPath = DependencyLocator.FindPython();
        var ffmpegPath = DependencyLocator.FindFfmpeg();
        return new BootstrapDiagnostics(
            pythonPath is not null, pythonPath,
            ffmpegPath is not null, ffmpegPath);
    }
}
