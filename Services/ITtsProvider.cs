using System.Threading;
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
public interface ITtsProvider
{
    /// <summary>
    /// Generates speech for all translated segments in a translation JSON,
    /// returning the combined output audio stream.
    /// </summary>
    Task<TtsResult> GenerateTtsAsync(
        TtsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates speech for a single translated text payload, returning
    /// the output audio stream. Primary path for dubbing workflows.
    /// </summary>
    Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default);
}
