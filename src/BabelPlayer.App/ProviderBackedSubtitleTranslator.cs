using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class ProviderBackedSubtitleTranslator : ISubtitleTranslator
{
    private readonly ProviderAvailabilityContext _context;
    private readonly TranslationProviderRegistry _registry;
    private readonly ITranslationEngineFactory _translationEngineFactory;
    private readonly ILocalModelRuntime _localRuntime;
    private readonly IBabelLogger _logger;

    public ProviderBackedSubtitleTranslator(
        ProviderAvailabilityContext context,
        TranslationProviderRegistry registry,
        ITranslationEngineFactory translationEngineFactory,
        ILocalModelRuntime localRuntime,
        IBabelLogFactory? logFactory = null)
    {
        _context = context;
        _registry = registry;
        _translationEngineFactory = translationEngineFactory;
        _localRuntime = localRuntime;
        _logger = (logFactory ?? context.LogFactory ?? NullBabelLogFactory.Instance).CreateLogger("subtitles.translation");
    }

    public event Action<LocalTranslationRuntimeStatus>? RuntimeStatusChanged;

    public async Task WarmupAsync(TranslationModelSelection selection, CancellationToken cancellationToken)
    {
        if (selection.Provider is not (TranslationProvider.LocalHyMt15_1_8B or TranslationProvider.LocalHyMt15_7B))
        {
            return;
        }

        _logger.LogInfo("Translation runtime warmup starting.", BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider)));
        var service = _translationEngineFactory.Create(_context.LogFactory);
        service.LocalRuntimeStatusChanged += HandleRuntimeStatus;
        service.ConfigureLocal(selection.Provider switch
        {
            TranslationProvider.LocalHyMt15_1_8B => new LocalTranslationOptions(
                OfflineTranslationModel.HyMt15_1_8B,
                _localRuntime.ResolveExecutablePath(_context)),
            TranslationProvider.LocalHyMt15_7B => new LocalTranslationOptions(
                OfflineTranslationModel.HyMt15_7B,
                _localRuntime.ResolveExecutablePath(_context)),
            _ => new LocalTranslationOptions(OfflineTranslationModel.None)
        });

        try
        {
            await service.WarmupLocalRuntimeAsync(cancellationToken);
            _logger.LogInfo("Translation runtime warmup completed.", BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider)));
        }
        finally
        {
            service.LocalRuntimeStatusChanged -= HandleRuntimeStatus;
        }
    }

    public async Task<string> TranslateAsync(
        TranslationModelSelection selection,
        string text,
        CancellationToken cancellationToken)
    {
        var translated = await TranslateBatchAsync(selection, [text], cancellationToken);
        return translated[0];
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationModelSelection selection,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        if (selection.Provider == TranslationProvider.None)
        {
            return texts.ToArray();
        }

        if (!_registry.TryGetProvider(selection.Provider, out var provider) || provider is null)
        {
            throw new InvalidOperationException($"No translation provider adapter is registered for {selection.Provider}.");
        }

        _logger.LogInfo("Translation batch starting.", BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider), ("textCount", texts.Count)));
        try
        {
            var result = await provider.TranslateBatchAsync(
                new TranslationRequest(selection, texts, "en"),
                _context,
                cancellationToken);
            _logger.LogInfo("Translation batch completed.", BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider), ("textCount", texts.Count)));
            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Translation batch failed.", ex, BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider), ("textCount", texts.Count)));
            throw;
        }
    }

    private void HandleRuntimeStatus(LocalTranslationRuntimeStatus status)
    {
        RuntimeStatusChanged?.Invoke(status);
    }
}
