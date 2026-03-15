using BabelPlayer.App;
using BabelPlayer.Core;
using CoreTranslationProvider = BabelPlayer.Core.ITranslationProvider;

namespace BabelPlayer.Infrastructure;

public sealed class ProviderCompositionFactory : IProviderCompositionFactory
{
    private readonly ITranscriptionEngineFactory _transcriptionEngineFactory;
    private readonly ITranslationEngineFactory _translationEngineFactory;

    public ProviderCompositionFactory(
        ITranscriptionEngineFactory? transcriptionEngineFactory = null,
        ITranslationEngineFactory? translationEngineFactory = null)
    {
        _transcriptionEngineFactory = transcriptionEngineFactory ?? new AsrTranscriptionEngineFactory();
        _translationEngineFactory = translationEngineFactory ?? new MtTranslationEngineFactory();
    }

    public ProviderAvailabilityComposition Create(
        ICredentialStore credentialStore,
        Func<string, string?> environmentVariableReader,
        IBabelLogFactory? logFactory = null)
    {
        return new ProviderAvailabilityComposition(
            new ProviderAvailabilityContext(credentialStore, environmentVariableReader, logFactory ?? NullBabelLogFactory.Instance),
            new TranscriptionProviderRegistry(
            [
                new WhisperLocalTranscriptionProvider(_transcriptionEngineFactory),
                new WindowsSpeechFallbackTranscriptionProvider(_transcriptionEngineFactory),
                new OpenAiTranscriptionProviderAdapter(_transcriptionEngineFactory)
            ]),
            new TranslationProviderRegistry(
            [
                new OpenAiTranslationProviderAdapter(_translationEngineFactory),
                new GoogleTranslationProviderAdapter(_translationEngineFactory),
                new DeepLTranslationProviderAdapter(_translationEngineFactory),
                new MicrosoftTranslationProviderAdapter(_translationEngineFactory),
                new LocalLlamaTranslationProviderAdapter(TranslationProvider.LocalHyMt15_1_8B, _translationEngineFactory),
                new LocalLlamaTranslationProviderAdapter(TranslationProvider.LocalHyMt15_7B, _translationEngineFactory)
            ]),
            new LlamaCppRuntimeAdapter());
    }
}

internal sealed class WhisperLocalTranscriptionProvider : ITranscriptionProvider
{
    private readonly ITranscriptionEngineFactory _engineFactory;

    public WhisperLocalTranscriptionProvider(ITranscriptionEngineFactory engineFactory)
    {
        _engineFactory = engineFactory;
    }

    public string Id => "local-whisper";

    public TranscriptionProvider Provider => TranscriptionProvider.Local;

    public bool CanHandle(TranscriptionModelSelection selection) => selection.Provider == TranscriptionProvider.Local;

    public bool IsAvailable(TranscriptionModelSelection selection, ProviderAvailabilityContext context) => true;

    public Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(
        TranscriptionRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
        => _engineFactory.Create(request, context).TranscribeWithWhisperAsync(request.VideoPath, request.Options, cancellationToken);
}

internal sealed class WindowsSpeechFallbackTranscriptionProvider : ITranscriptionProvider
{
    private readonly ITranscriptionEngineFactory _engineFactory;

    public WindowsSpeechFallbackTranscriptionProvider(ITranscriptionEngineFactory engineFactory)
    {
        _engineFactory = engineFactory;
    }

    public string Id => "windows-speech";

    public TranscriptionProvider Provider => TranscriptionProvider.Local;

    public bool CanHandle(TranscriptionModelSelection selection) => selection.Provider == TranscriptionProvider.Local;

    public bool IsAvailable(TranscriptionModelSelection selection, ProviderAvailabilityContext context) => true;

    public Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(
        TranscriptionRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
        => _engineFactory.Create(request, context).TranscribeWithWindowsSpeechAsync(
            request.VideoPath,
            request.Options.LanguageHint,
            cancellationToken);
}

internal sealed class OpenAiTranscriptionProviderAdapter : ITranscriptionProvider
{
    private readonly ITranscriptionEngineFactory _engineFactory;

    public OpenAiTranscriptionProviderAdapter(ITranscriptionEngineFactory engineFactory)
    {
        _engineFactory = engineFactory;
    }

    public string Id => "openai-transcription";

    public TranscriptionProvider Provider => TranscriptionProvider.Cloud;

    public bool CanHandle(TranscriptionModelSelection selection) => selection.Provider == TranscriptionProvider.Cloud;

    public bool IsAvailable(TranscriptionModelSelection selection, ProviderAvailabilityContext context)
        => ProviderAvailabilityHelpers.HasOpenAiApiKey(context);

    public Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(
        TranscriptionRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        var apiKey = context.EnvironmentVariableReader("OPENAI_API_KEY") ?? context.CredentialStore.GetOpenAiApiKey();
        var options = new CaptionGenerationOptions
        {
            Mode = request.Options.Mode,
            LanguageHint = request.Options.LanguageHint,
            OpenAiApiKey = apiKey,
            LocalModelType = request.Options.LocalModelType,
            CloudModel = request.Options.CloudModel
        };
        return _engineFactory.Create(request, context).TranscribeWithOpenAiAsync(request.VideoPath, options, cancellationToken);
    }
}

internal sealed class OpenAiTranslationProviderAdapter : CoreTranslationProvider
{
    private readonly ITranslationEngineFactory _engineFactory;

    public OpenAiTranslationProviderAdapter(ITranslationEngineFactory engineFactory)
    {
        _engineFactory = engineFactory;
    }

    public TranslationProvider Provider => TranslationProvider.OpenAi;

    public bool IsConfigured(ProviderAvailabilityContext context)
        => ProviderAvailabilityHelpers.HasOpenAiApiKey(context);

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        var apiKey = context.EnvironmentVariableReader("OPENAI_API_KEY") ?? context.CredentialStore.GetOpenAiApiKey();
        var service = _engineFactory.Create(context.LogFactory);
        service.ConfigureCloud(new CloudTranslationOptions(CloudTranslationProvider.OpenAi, apiKey!.Trim(), request.Selection.CloudModel));
        service.ConfigureLocal(null);
        return await service.TranslateBatchAsync(request.Texts, cancellationToken);
    }
}

internal sealed class GoogleTranslationProviderAdapter : CoreTranslationProvider
{
    private readonly ITranslationEngineFactory _engineFactory;

    public GoogleTranslationProviderAdapter(ITranslationEngineFactory engineFactory)
    {
        _engineFactory = engineFactory;
    }

    public TranslationProvider Provider => TranslationProvider.Google;

    public bool IsConfigured(ProviderAvailabilityContext context)
        => ProviderAvailabilityHelpers.TryGetGoogleOptions(context) is not null;

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        var service = _engineFactory.Create(context.LogFactory);
        service.ConfigureCloud(ProviderAvailabilityHelpers.TryGetGoogleOptions(context));
        service.ConfigureLocal(null);
        return await service.TranslateBatchAsync(request.Texts, cancellationToken);
    }
}

internal sealed class DeepLTranslationProviderAdapter : CoreTranslationProvider
{
    private readonly ITranslationEngineFactory _engineFactory;

    public DeepLTranslationProviderAdapter(ITranslationEngineFactory engineFactory)
    {
        _engineFactory = engineFactory;
    }

    public TranslationProvider Provider => TranslationProvider.DeepL;

    public bool IsConfigured(ProviderAvailabilityContext context)
        => ProviderAvailabilityHelpers.TryGetDeepLOptions(context) is not null;

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        var service = _engineFactory.Create(context.LogFactory);
        service.ConfigureCloud(ProviderAvailabilityHelpers.TryGetDeepLOptions(context));
        service.ConfigureLocal(null);
        return await service.TranslateBatchAsync(request.Texts, cancellationToken);
    }
}

internal sealed class MicrosoftTranslationProviderAdapter : CoreTranslationProvider
{
    private readonly ITranslationEngineFactory _engineFactory;

    public MicrosoftTranslationProviderAdapter(ITranslationEngineFactory engineFactory)
    {
        _engineFactory = engineFactory;
    }

    public TranslationProvider Provider => TranslationProvider.MicrosoftTranslator;

    public bool IsConfigured(ProviderAvailabilityContext context)
        => ProviderAvailabilityHelpers.TryGetMicrosoftOptions(context) is not null;

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        var service = _engineFactory.Create(context.LogFactory);
        service.ConfigureCloud(ProviderAvailabilityHelpers.TryGetMicrosoftOptions(context));
        service.ConfigureLocal(null);
        return await service.TranslateBatchAsync(request.Texts, cancellationToken);
    }
}

internal sealed class LocalLlamaTranslationProviderAdapter : CoreTranslationProvider
{
    private readonly ITranslationEngineFactory _engineFactory;

    public LocalLlamaTranslationProviderAdapter(
        TranslationProvider provider,
        ITranslationEngineFactory engineFactory)
    {
        Provider = provider;
        _engineFactory = engineFactory;
    }

    public TranslationProvider Provider { get; }

    public bool IsConfigured(ProviderAvailabilityContext context)
        => ProviderAvailabilityHelpers.ResolveLlamaCppServerPath(context) is not null;

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        var model = Provider switch
        {
            TranslationProvider.LocalHyMt15_1_8B => OfflineTranslationModel.HyMt15_1_8B,
            TranslationProvider.LocalHyMt15_7B => OfflineTranslationModel.HyMt15_7B,
            _ => OfflineTranslationModel.None
        };

        var service = _engineFactory.Create(context.LogFactory);
        service.ConfigureCloud(null);
        service.ConfigureLocal(new LocalTranslationOptions(model, ProviderAvailabilityHelpers.ResolveLlamaCppServerPath(context)));
        return await service.TranslateBatchAsync(request.Texts, cancellationToken);
    }
}

internal sealed class LlamaCppRuntimeAdapter : ILocalModelRuntime
{
    public string RuntimeId => "llama.cpp";

    public string? ResolveExecutablePath(ProviderAvailabilityContext context)
        => ProviderAvailabilityHelpers.ResolveLlamaCppServerPath(context);
}
