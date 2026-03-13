using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.Infrastructure;

public sealed class AsrTranscriptionEngineFactory : ITranscriptionEngineFactory
{
    public ITranscriptionEngine Create(TranscriptionRequest request, ProviderAvailabilityContext context)
    {
        var service = new AsrService(
            request.Options.Mode == CaptionTranscriptionMode.Cloud ? "transcription.cloud" : "transcription.local",
            context.LogFactory);
        if (request.OnFinal is not null)
        {
            service.OnFinal += request.OnFinal;
        }

        if (request.OnProgress is not null)
        {
            service.OnModelTransferProgress += request.OnProgress;
        }

        return new AsrTranscriptionEngine(service);
    }

    private sealed class AsrTranscriptionEngine : ITranscriptionEngine
    {
        private readonly AsrService _service;

        public AsrTranscriptionEngine(AsrService service)
        {
            _service = service;
        }

        public Task<IReadOnlyList<SubtitleCue>> TranscribeWithWhisperAsync(
            string videoPath,
            CaptionGenerationOptions options,
            CancellationToken cancellationToken)
        {
            return _service.TranscribeVideoWithWhisperAsync(
                videoPath,
                options.LocalModelType ?? Whisper.net.Ggml.GgmlType.BaseEn,
                options.LanguageHint,
                cancellationToken);
        }

        public Task<IReadOnlyList<SubtitleCue>> TranscribeWithWindowsSpeechAsync(
            string videoPath,
            string? languageHint,
            CancellationToken cancellationToken)
            => _service.TranscribeVideoWithWindowsSpeechAsync(videoPath, languageHint, cancellationToken);

        public Task<IReadOnlyList<SubtitleCue>> TranscribeWithOpenAiAsync(
            string videoPath,
            CaptionGenerationOptions options,
            CancellationToken cancellationToken)
            => _service.TranscribeVideoWithOpenAiAsync(videoPath, options, cancellationToken);
    }
}

public sealed class MtTranslationEngineFactory : ITranslationEngineFactory
{
    public ITranslationEngine Create(IBabelLogFactory? logFactory = null)
        => new MtTranslationEngineAdapter(new MtService(logFactory));

    private sealed class MtTranslationEngineAdapter : ITranslationEngine
    {
        private readonly MtService _service;

        public MtTranslationEngineAdapter(MtService service)
        {
            _service = service;
            _service.OnLocalRuntimeStatus += status => LocalRuntimeStatusChanged?.Invoke(status);
        }

        public event Action<LocalTranslationRuntimeStatus>? LocalRuntimeStatusChanged;

        public void ConfigureCloud(CloudTranslationOptions? cloud) => _service.ConfigureCloud(cloud);

        public void ConfigureLocal(LocalTranslationOptions? local) => _service.ConfigureLocal(local);

        public Task WarmupLocalRuntimeAsync(CancellationToken cancellationToken)
            => _service.WarmupLocalRuntimeAsync(cancellationToken);

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken)
            => _service.TranslateBatchAsync(texts, cancellationToken);
    }
}
