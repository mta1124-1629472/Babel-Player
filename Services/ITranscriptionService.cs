using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Transcribes audio into timed text segments.
/// Implementations are provider-specific (e.g. faster-whisper).
/// </summary>
public interface ITranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(
        string audioPath,
        string outputJsonPath,
        string model = "base",
        CancellationToken cancellationToken = default);
}
