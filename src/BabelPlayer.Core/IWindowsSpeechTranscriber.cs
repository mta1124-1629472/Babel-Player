namespace BabelPlayer.Core;

/// <summary>
/// Abstracts the Windows Speech Recognition fallback so call sites
/// in cross-platform code never reference the [SupportedOSPlatform("windows")] API directly.
/// </summary>
public interface IWindowsSpeechTranscriber
{
    /// <summary>True when at least one speech recognizer is installed on the current platform.</summary>
    bool IsAvailable { get; }

    Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(
        string wavePath,
        string? languageHint,
        CancellationToken cancellationToken);
}
