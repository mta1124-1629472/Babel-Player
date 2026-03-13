using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed record ProviderAvailabilityContext(
    CredentialFacade CredentialFacade,
    Func<string, string?> EnvironmentVariableReader,
    IBabelLogFactory? LogFactory = null);

public sealed record TranscriptionRequest(
    string VideoPath,
    CaptionGenerationOptions Options,
    Action<TranscriptChunk>? OnFinal = null,
    Action<ModelTransferProgress>? OnProgress = null);

public sealed record TranslationRequest(
    TranslationModelSelection Selection,
    IReadOnlyList<string> Texts,
    string TargetLanguage);

public interface ITranscriptionProvider
{
    string Id { get; }

    TranscriptionProvider Provider { get; }

    bool CanHandle(TranscriptionModelSelection selection);

    bool IsAvailable(TranscriptionModelSelection selection, ProviderAvailabilityContext context);

    Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(
        TranscriptionRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken);
}

public interface ITranslationProvider
{
    TranslationProvider Provider { get; }

    bool IsConfigured(ProviderAvailabilityContext context);

    Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken);
}

public interface ILocalModelRuntime
{
    string RuntimeId { get; }

    string? ResolveExecutablePath(ProviderAvailabilityContext context);
}

public sealed class TranscriptionProviderRegistry
{
    private readonly IReadOnlyList<ITranscriptionProvider> _providers;

    public TranscriptionProviderRegistry(IReadOnlyList<ITranscriptionProvider> providers)
    {
        _providers = providers;
    }

    public IReadOnlyList<ITranscriptionProvider> ResolveProviders(
        TranscriptionModelSelection selection,
        ProviderAvailabilityContext context)
    {
        return _providers
            .Where(provider => provider.Provider == selection.Provider
                && provider.CanHandle(selection)
                && provider.IsAvailable(selection, context))
            .ToArray();
    }
}

public sealed class TranslationProviderRegistry
{
    private readonly IReadOnlyDictionary<TranslationProvider, ITranslationProvider> _providers;

    public TranslationProviderRegistry(IReadOnlyList<ITranslationProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Provider);
    }

    public bool TryGetProvider(TranslationProvider provider, out ITranslationProvider? translationProvider)
    {
        return _providers.TryGetValue(provider, out translationProvider);
    }

    public IReadOnlyList<ITranslationProvider> Providers => _providers.Values.ToArray();
}

public interface IProviderAvailabilityService
{
    string ResolvePersistedTranscriptionModelKey(string? modelKey);

    string? ResolvePersistedTranslationModelKey(string? modelKey);

    bool IsTranslationProviderConfigured(TranslationProvider provider);

    string? ResolveLlamaCppServerPath();
}

public sealed record ProviderAvailabilityComposition(
    ProviderAvailabilityContext Context,
    TranscriptionProviderRegistry TranscriptionRegistry,
    TranslationProviderRegistry TranslationRegistry,
    ILocalModelRuntime LocalRuntime);

public sealed class ProviderAvailabilityService : IProviderAvailabilityService
{
    private readonly ProviderAvailabilityComposition _composition;

    public ProviderAvailabilityService(
        IProviderCompositionFactory compositionFactory,
        CredentialFacade credentialFacade,
        Func<string, string?> environmentVariableReader,
        IBabelLogFactory? logFactory = null)
        : this(compositionFactory.Create(credentialFacade, environmentVariableReader, logFactory))
    {
    }

    public ProviderAvailabilityService(ProviderAvailabilityComposition composition)
    {
        _composition = composition;
    }

    public ProviderAvailabilityComposition Composition => _composition;

    internal ProviderAvailabilityContext Context => _composition.Context;

    internal TranscriptionProviderRegistry TranscriptionRegistry => _composition.TranscriptionRegistry;

    internal TranslationProviderRegistry TranslationRegistry => _composition.TranslationRegistry;

    public string ResolvePersistedTranscriptionModelKey(string? modelKey)
    {
        var selection = SubtitleWorkflowCatalog.GetTranscriptionModel(modelKey);
        return _composition.TranscriptionRegistry.ResolveProviders(selection, _composition.Context).Count > 0
            ? selection.Key
            : SubtitleWorkflowCatalog.DefaultTranscriptionModelKey;
    }

    public string? ResolvePersistedTranslationModelKey(string? modelKey)
    {
        var selection = SubtitleWorkflowCatalog.GetTranslationModel(modelKey);
        if (selection.Provider == TranslationProvider.None)
        {
            return null;
        }

        return IsTranslationProviderConfigured(selection.Provider) ? selection.Key : null;
    }

    public bool IsTranslationProviderConfigured(TranslationProvider provider)
    {
        if (provider == TranslationProvider.None)
        {
            return false;
        }

        return _composition.TranslationRegistry.TryGetProvider(provider, out var adapter)
               && adapter is not null
               && adapter.IsConfigured(_composition.Context);
    }

    public string? ResolveLlamaCppServerPath()
    {
        return _composition.LocalRuntime.ResolveExecutablePath(_composition.Context);
    }
}
