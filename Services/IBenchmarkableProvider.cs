namespace Babel.Player.Services;

/// <summary>
/// Extends <see cref="ITranscriptionProvider"/> with a stable, human-readable
/// provider identity used in benchmark result files and matrix IDs.
///
/// Implement this on any provider that participates in the benchmark suite so
/// that result files carry a consistent identifier rather than a CLR type name.
/// </summary>
public interface IBenchmarkableProvider
{
    /// <summary>
    /// Stable provider identifier used in benchmark output (e.g. <c>"faster-whisper"</c>).
    /// Must be lowercase kebab-case and version-stable.
    /// </summary>
    string ProviderId { get; }
}
