using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// No-op implementation used on Linux / macOS where Windows Speech is unavailable.
/// </summary>
public sealed class NullWindowsSpeechTranscriber : IWindowsSpeechTranscriber
{
    public bool IsAvailable => false;

    public Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(
        string wavePath,
        string? languageHint,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SubtitleCue>>(Array.Empty<SubtitleCue>());
}
