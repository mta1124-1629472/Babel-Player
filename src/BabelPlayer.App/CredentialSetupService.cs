using BabelPlayer.Core;

namespace BabelPlayer.App;

public interface ICredentialSetupService
{
    event Action<CredentialSetupSnapshot>? SnapshotChanged;

    CredentialSetupSnapshot Current { get; }

    CredentialSelectionAvailability GetTranscriptionAvailability(TranscriptionModelSelection selection);

    CredentialSelectionAvailability GetTranslationAvailability(TranslationModelSelection selection);

    Task<bool> EnsureOpenAiCredentialsAsync(CancellationToken cancellationToken = default);

    Task<bool> EnsureTranslationProviderCredentialsAsync(TranslationProvider provider, CancellationToken cancellationToken = default);

    Task<bool> EnsureLlamaCppRuntimeReadyAsync(CancellationToken cancellationToken = default);

    Task<string> EnsureFfmpegAsync(CancellationToken cancellationToken = default);
}

public sealed record CredentialSetupSnapshot
{
    public bool HasOpenAiCredentials { get; init; }
    public string? LlamaCppServerPath { get; init; }
    public IReadOnlyDictionary<TranslationProvider, bool> TranslationProviderConfigured { get; init; }
        = new Dictionary<TranslationProvider, bool>();
    public CredentialSelectionAvailability SelectedTranscription { get; init; } = new(string.Empty, false, false, false);
    public CredentialSelectionAvailability SelectedTranslation { get; init; } = new(string.Empty, false, false, false);
}

public sealed record CredentialSelectionAvailability(
    string ModelKey,
    bool IsAvailable,
    bool RequiresCredentials,
    bool RequiresRuntimeBootstrap,
    string? Hint = null);

public sealed class CredentialSetupService : ICredentialSetupService
{
    private readonly ICredentialStore _credentialStore;
    private readonly IProviderAvailabilityService _providerAvailabilityService;
    private readonly IAiCredentialCoordinator _aiCredentialCoordinator;
    private readonly IRuntimeProvisioner _runtimeProvisioner;
    private readonly Func<string, string?> _environmentVariableReader;

    public CredentialSetupService(
        ICredentialStore credentialStore,
        IProviderAvailabilityService providerAvailabilityService,
        IAiCredentialCoordinator aiCredentialCoordinator,
        IRuntimeProvisioner runtimeProvisioner,
        Func<string, string?> environmentVariableReader)
    {
        _credentialStore = credentialStore;
        _providerAvailabilityService = providerAvailabilityService;
        _aiCredentialCoordinator = aiCredentialCoordinator;
        _runtimeProvisioner = runtimeProvisioner;
        _environmentVariableReader = environmentVariableReader;
        Current = BuildSnapshot();
    }

    public event Action<CredentialSetupSnapshot>? SnapshotChanged;

    public CredentialSetupSnapshot Current { get; private set; }

    public CredentialSelectionAvailability GetTranscriptionAvailability(TranscriptionModelSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.Provider == TranscriptionProvider.Cloud)
        {
            var hasCredentials = HasOpenAiCredentials();
            return new CredentialSelectionAvailability(
                selection.Key,
                hasCredentials,
                RequiresCredentials: true,
                RequiresRuntimeBootstrap: false,
                hasCredentials ? null : "OpenAI credentials are required.");
        }

        return new CredentialSelectionAvailability(
            selection.Key,
            IsAvailable: true,
            RequiresCredentials: false,
            RequiresRuntimeBootstrap: false);
    }

    public CredentialSelectionAvailability GetTranslationAvailability(TranslationModelSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.Provider == TranslationProvider.None)
        {
            return new CredentialSelectionAvailability(selection.Key, true, false, false);
        }

        if (selection.Provider is TranslationProvider.LocalHyMt15_1_8B or TranslationProvider.LocalHyMt15_7B)
        {
            var runtimeReady = !string.IsNullOrWhiteSpace(_providerAvailabilityService.ResolveLlamaCppServerPath());
            return new CredentialSelectionAvailability(
                selection.Key,
                runtimeReady,
                RequiresCredentials: false,
                RequiresRuntimeBootstrap: true,
                runtimeReady ? null : "llama.cpp runtime setup is required.");
        }

        var configured = _providerAvailabilityService.IsTranslationProviderConfigured(selection.Provider);
        return new CredentialSelectionAvailability(
            selection.Key,
            configured,
            RequiresCredentials: true,
            RequiresRuntimeBootstrap: false,
            configured ? null : "Provider credentials are required.");
    }

    public async Task<bool> EnsureOpenAiCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var ensured = await _aiCredentialCoordinator.EnsureOpenAiApiKeyAsync(cancellationToken);
        Refresh();
        return ensured;
    }

    public async Task<bool> EnsureTranslationProviderCredentialsAsync(TranslationProvider provider, CancellationToken cancellationToken = default)
    {
        var ensured = await _aiCredentialCoordinator.EnsureTranslationProviderCredentialsAsync(provider, cancellationToken);
        Refresh();
        return ensured;
    }

    public async Task<bool> EnsureLlamaCppRuntimeReadyAsync(CancellationToken cancellationToken = default)
    {
        var ensured = await _runtimeProvisioner.EnsureLlamaCppRuntimeReadyAsync(onProgress: null, cancellationToken);
        Refresh();
        return ensured;
    }

    public async Task<string> EnsureFfmpegAsync(CancellationToken cancellationToken = default)
    {
        var path = await _runtimeProvisioner.EnsureFfmpegAsync(onProgress: null, cancellationToken);
        Refresh();
        return path;
    }

    private void Refresh()
    {
        Current = BuildSnapshot();
        SnapshotChanged?.Invoke(Current);
    }

    private CredentialSetupSnapshot BuildSnapshot()
    {
        var translationProviders = Enum.GetValues<TranslationProvider>()
            .Where(provider => provider != TranslationProvider.None
                && provider is not (TranslationProvider.LocalHyMt15_1_8B or TranslationProvider.LocalHyMt15_7B))
            .ToDictionary(
                provider => provider,
                provider => _providerAvailabilityService.IsTranslationProviderConfigured(provider));
        var selectedTranscription = SubtitleWorkflowCatalog.GetTranscriptionModel(
            _providerAvailabilityService.ResolvePersistedTranscriptionModelKey(_credentialStore.GetSubtitleModelKey()));
        var selectedTranslationKey = _providerAvailabilityService.ResolvePersistedTranslationModelKey(_credentialStore.GetTranslationModelKey());
        var selectedTranslation = SubtitleWorkflowCatalog.GetTranslationModel(selectedTranslationKey);

        return new CredentialSetupSnapshot
        {
            HasOpenAiCredentials = HasOpenAiCredentials(),
            LlamaCppServerPath = _providerAvailabilityService.ResolveLlamaCppServerPath(),
            TranslationProviderConfigured = translationProviders,
            SelectedTranscription = GetTranscriptionAvailability(selectedTranscription),
            SelectedTranslation = GetTranslationAvailability(selectedTranslation)
        };
    }

    private bool HasOpenAiCredentials()
    {
        return !string.IsNullOrWhiteSpace(_environmentVariableReader("OPENAI_API_KEY"))
            || !string.IsNullOrWhiteSpace(_credentialStore.GetOpenAiApiKey());
    }
}
