using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Synthesises speech from translated dialogue segments.
/// Implementations are provider-specific (e.g. edge-tts, piper).
/// Provider configuration (model directory, API credentials) belongs in the
/// constructor — method signatures are uniform across all implementations.
///
/// Note on multi-speaker workflows: <see cref="GenerateSegmentTtsAsync"/> is the
/// primary path. The caller controls which voice each segment receives, enabling
/// per-speaker voice assignment without interface changes.
/// <see cref="GenerateTtsAsync"/> is a convenience method for single-voice output.
/// </summary>
public interface ITtsService
{
    /// <summary>
    /// Generates speech for all translated segments in a translation JSON,
    /// combined into a single audio file at <paramref name="outputAudioPath"/>.
    /// Primary use: single-voice output and smoke testing.
    /// </summary>
    Task<TtsResult> GenerateTtsAsync(
        string translationJsonPath,
        string outputAudioPath,
        string voice);

    /// <summary>
    /// Generates speech for a single piece of text and writes it to
    /// <paramref name="outputAudioPath"/>. The caller determines which voice
    /// each segment receives, making this the primary method for multi-speaker
    /// workflows.
    /// </summary>
    Task<TtsResult> GenerateSegmentTtsAsync(
        string text,
        string outputAudioPath,
        string voice);
}
