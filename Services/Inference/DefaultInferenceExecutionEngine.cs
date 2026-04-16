using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Services.Registries;

namespace Babel.Player.Services;

/// <summary>
/// Pass-through implementation: forwards to the supplied provider without altering behavior.
/// </summary>
public sealed class DefaultInferenceExecutionEngine : IInferenceExecutionEngine
{
    public static DefaultInferenceExecutionEngine Instance { get; } = new();

    private DefaultInferenceExecutionEngine() { }

    public Task<TranscriptionResult> TranscribeAsync(
        ITranscriptionProvider provider,
        TranscriptionRequest request,
        CancellationToken cancellationToken = default) =>
        provider.TranscribeAsync(request, cancellationToken);

    public Task<TranscriptionResult> TranscribeStreamingAsync(
        IStreamingTranscriptionProvider provider,
        TranscriptionRequest request,
        ChannelWriter<TranscriptChannelItem> writer,
        CancellationToken cancellationToken = default) =>
        provider.TranscribeStreamingAsync(request, writer, cancellationToken);

    public Task<TranslationResult> TranslateAsync(
        ITranslationProvider provider,
        TranslationRequest request,
        CancellationToken cancellationToken = default) =>
        provider.TranslateAsync(request, cancellationToken);

    public Task<TranslationResult> TranslateSingleSegmentAsync(
        ITranslationProvider provider,
        SingleSegmentTranslationRequest request,
        CancellationToken cancellationToken = default) =>
        provider.TranslateSingleSegmentAsync(request, cancellationToken);

    public Task<TtsResult> GenerateSegmentTtsAsync(
        ITtsProvider provider,
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default) =>
        provider.GenerateSegmentTtsAsync(request, cancellationToken);

    public Task<DiarizationResult> DiarizeAsync(
        IDiarizationProvider provider,
        DiarizationRequest request,
        CancellationToken cancellationToken = default) =>
        provider.DiarizeAsync(request, cancellationToken);
}
