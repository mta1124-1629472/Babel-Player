using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Services.Registries;

namespace Babel.Player.Services;

/// <summary>
/// Single seam for invoking inference providers from the session workflow. Production uses
/// <see cref="DefaultInferenceExecutionEngine"/> (direct delegation); tests may substitute fakes.
/// </summary>
public interface IInferenceExecutionEngine
{
    Task<TranscriptionResult> TranscribeAsync(
        ITranscriptionProvider provider,
        TranscriptionRequest request,
        CancellationToken cancellationToken = default);

    Task<TranscriptionResult> TranscribeStreamingAsync(
        IStreamingTranscriptionProvider provider,
        TranscriptionRequest request,
        ChannelWriter<TranscriptChannelItem> writer,
        CancellationToken cancellationToken = default);

    Task<TranslationResult> TranslateAsync(
        ITranslationProvider provider,
        TranslationRequest request,
        CancellationToken cancellationToken = default);

    Task<TranslationResult> TranslateSingleSegmentAsync(
        ITranslationProvider provider,
        SingleSegmentTranslationRequest request,
        CancellationToken cancellationToken = default);

    Task<TtsResult> GenerateSegmentTtsAsync(
        ITtsProvider provider,
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default);

    Task<DiarizationResult> DiarizeAsync(
        IDiarizationProvider provider,
        DiarizationRequest request,
        CancellationToken cancellationToken = default);
}
