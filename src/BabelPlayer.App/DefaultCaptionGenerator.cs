using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class DefaultCaptionGenerator : ICaptionGenerator
{
    private readonly ProviderAvailabilityContext _context;
    private readonly TranscriptionProviderRegistry _registry;
    private readonly IBabelLogger _logger;

    public DefaultCaptionGenerator(ProviderAvailabilityContext context, TranscriptionProviderRegistry registry, IBabelLogFactory? logFactory = null)
    {
        _context = context;
        _registry = registry;
        _logger = (logFactory ?? context.LogFactory ?? NullBabelLogFactory.Instance).CreateLogger("subtitles.captions");
    }

    public async Task<IReadOnlyList<SubtitleCue>> GenerateCaptionsAsync(
        string videoPath,
        TranscriptionModelSelection selection,
        string? languageHint,
        Action<TranscriptChunk>? onFinal,
        Action<ModelTransferProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        List<Exception>? failures = null;
        var providers = _registry.ResolveProviders(selection, _context);
        var options = new CaptionGenerationOptions
        {
            Mode = selection.Provider == TranscriptionProvider.Cloud ? CaptionTranscriptionMode.Cloud : CaptionTranscriptionMode.Local,
            LanguageHint = languageHint,
            OpenAiApiKey = _context.EnvironmentVariableReader("OPENAI_API_KEY") ?? _context.CredentialFacade.GetOpenAiApiKey(),
            LocalModelType = SubtitleWorkflowCatalog.ResolveLocalModelType(selection.LocalModelKey),
            CloudModel = selection.CloudModel
        };
        _logger.LogInfo("Caption generation starting.", BabelLogContext.Create(("videoPath", videoPath), ("modelKey", selection.Key), ("provider", selection.Provider), ("languageHint", languageHint)));

        foreach (var provider in providers)
        {
            try
            {
                var result = await provider.TranscribeAsync(
                    new TranscriptionRequest(videoPath, options, onFinal, onProgress),
                    _context,
                    cancellationToken);
                _logger.LogInfo("Caption generation completed.", BabelLogContext.Create(("videoPath", videoPath), ("modelKey", selection.Key), ("cueCount", result.Count), ("providerAdapter", provider.Id)));
                return result;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failures ??= [];
                failures.Add(ex);
                _logger.LogWarning("Caption generation provider failed.", ex, BabelLogContext.Create(("videoPath", videoPath), ("modelKey", selection.Key), ("providerAdapter", provider.Id)));
            }
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException($"No transcription provider completed for {selection.DisplayName}.", failures);
        }

        throw new InvalidOperationException($"No transcription provider is available for {selection.DisplayName}.");
    }
}
