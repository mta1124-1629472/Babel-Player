using System.Runtime.Versioning;
using System.Speech.Recognition;
using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Windows-only implementation that delegates to the Windows Speech Recognition engine.
/// All CA1416-sensitive code is contained here so no other project needs the annotation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSpeechTranscriber : IWindowsSpeechTranscriber
{
    public bool IsAvailable =>
        SpeechRecognitionEngine.InstalledRecognizers().Count > 0;

    public Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(
        string wavePath,
        string? languageHint,
        CancellationToken cancellationToken)
        => WindowsSpeechCore.TranscribeOnStaAsync(wavePath, languageHint, cancellationToken);
}
