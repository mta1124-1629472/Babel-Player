using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Transcribes audio into timed text segments.
/// Implementations are provider-specific (e.g. faster-whisper).
/// </summary>
public interface ITranscriptionProvider
{
    Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default);
}
