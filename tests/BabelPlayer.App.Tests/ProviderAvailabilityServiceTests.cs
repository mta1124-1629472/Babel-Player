using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

public sealed class ProviderAvailabilityServiceTests
{
    // ── TranslationProviderRegistry ───────────────────────────────────────────

    [Fact]
    public void TranslationProviderRegistry_TryGetProvider_ReturnsFalse_WhenNotRegistered()
    {
        var registry = new TranslationProviderRegistry([]);

        var found = registry.TryGetProvider(TranslationProvider.OpenAi, out var provider);

        Assert.False(found);
        Assert.Null(provider);
    }

    [Fact]
    public void TranslationProviderRegistry_TryGetProvider_ReturnsTrue_WhenRegistered()
    {
        var fakeProvider = new FakeTranslationProvider(TranslationProvider.OpenAi, isConfigured: true);
        var registry = new TranslationProviderRegistry([fakeProvider]);

        var found = registry.TryGetProvider(TranslationProvider.OpenAi, out var provider);

        Assert.True(found);
        Assert.NotNull(provider);
    }

    [Fact]
    public void TranslationProviderRegistry_Providers_ReturnsAllRegistered()
    {
        var providers = new List<ITranslationProvider>
        {
            new FakeTranslationProvider(TranslationProvider.OpenAi, true),
            new FakeTranslationProvider(TranslationProvider.DeepL, false)
        };
        var registry = new TranslationProviderRegistry(providers);

        Assert.Equal(2, registry.Providers.Count);
    }

    // ── TranscriptionProviderRegistry ─────────────────────────────────────────

    [Fact]
    public void TranscriptionProviderRegistry_ResolveProviders_ReturnsEmpty_WhenNoneMatch()
    {
        var registry = new TranscriptionProviderRegistry([]);
        var selection = SubtitleWorkflowCatalog.GetTranscriptionModel("local:tiny-multilingual");
        var context = new ProviderAvailabilityContext(new FakeCredentialStore(), _ => null);

        var result = registry.ResolveProviders(selection, context);

        Assert.Empty(result);
    }

    [Fact]
    public void TranscriptionProviderRegistry_ResolveProviders_ReturnsOnlyAvailableProviders()
    {
        var available = new FakeTranscriptionProvider(TranscriptionProvider.Local, isAvailable: true);
        var unavailable = new FakeTranscriptionProvider(TranscriptionProvider.Local, isAvailable: false);
        var registry = new TranscriptionProviderRegistry([available, unavailable]);
        var selection = SubtitleWorkflowCatalog.GetTranscriptionModel("local:tiny-multilingual");
        var context = new ProviderAvailabilityContext(new FakeCredentialStore(), _ => null);

        var result = registry.ResolveProviders(selection, context);

        Assert.Single(result);
    }

    // ── ProviderAvailabilityService ───────────────────────────────────────────

    [Fact]
    public void ProviderAvailabilityService_IsTranslationProviderConfigured_ReturnsFalse_ForNoneProvider()
    {
        var service = CreateService([]);

        var result = service.IsTranslationProviderConfigured(TranslationProvider.None);

        Assert.False(result);
    }

    [Fact]
    public void ProviderAvailabilityService_IsTranslationProviderConfigured_ReturnsFalse_WhenProviderNotInRegistry()
    {
        var service = CreateService([]);

        var result = service.IsTranslationProviderConfigured(TranslationProvider.OpenAi);

        Assert.False(result);
    }

    [Fact]
    public void ProviderAvailabilityService_IsTranslationProviderConfigured_ReturnsFalse_WhenProviderNotConfigured()
    {
        var provider = new FakeTranslationProvider(TranslationProvider.OpenAi, isConfigured: false);
        var service = CreateService([provider]);

        var result = service.IsTranslationProviderConfigured(TranslationProvider.OpenAi);

        Assert.False(result);
    }

    [Fact]
    public void ProviderAvailabilityService_IsTranslationProviderConfigured_ReturnsTrue_WhenProviderConfigured()
    {
        var provider = new FakeTranslationProvider(TranslationProvider.OpenAi, isConfigured: true);
        var service = CreateService([provider]);

        var result = service.IsTranslationProviderConfigured(TranslationProvider.OpenAi);

        Assert.True(result);
    }

    [Fact]
    public void ProviderAvailabilityService_ResolvePersistedTranslationModelKey_ReturnsNull_ForNoneProvider()
    {
        var service = CreateService([]);

        var result = service.ResolvePersistedTranslationModelKey(null);

        Assert.Null(result);
    }

    [Fact]
    public void ProviderAvailabilityService_ResolvePersistedTranslationModelKey_ReturnsNull_WhenProviderUnconfigured()
    {
        var provider = new FakeTranslationProvider(TranslationProvider.DeepL, isConfigured: false);
        var service = CreateService([provider]);

        var result = service.ResolvePersistedTranslationModelKey("cloud:deepl");

        Assert.Null(result);
    }

    [Fact]
    public void ProviderAvailabilityService_ResolvePersistedTranslationModelKey_ReturnsKey_WhenProviderConfigured()
    {
        var provider = new FakeTranslationProvider(TranslationProvider.DeepL, isConfigured: true);
        var service = CreateService([provider]);

        var result = service.ResolvePersistedTranslationModelKey("cloud:deepl");

        Assert.Equal("cloud:deepl", result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProviderAvailabilityService CreateService(IReadOnlyList<ITranslationProvider> translationProviders)
    {
        var credentialStore = new FakeCredentialStore();
        var context = new ProviderAvailabilityContext(credentialStore, _ => null);
        var transcriptionRegistry = new TranscriptionProviderRegistry([]);
        var translationRegistry = new TranslationProviderRegistry(translationProviders);
        var localRuntime = new FakeLocalRuntime();
        var composition = new ProviderAvailabilityComposition(context, transcriptionRegistry, translationRegistry, localRuntime);
        return new ProviderAvailabilityService(composition);
    }

    private sealed class FakeTranslationProvider : ITranslationProvider
    {
        private readonly bool _isConfigured;

        public FakeTranslationProvider(TranslationProvider provider, bool isConfigured)
        {
            Provider = provider;
            _isConfigured = isConfigured;
        }

        public TranslationProvider Provider { get; }

        public bool IsConfigured(ProviderAvailabilityContext context) => _isConfigured;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(TranslationRequest request, ProviderAvailabilityContext context, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeTranscriptionProvider : ITranscriptionProvider
    {
        private readonly bool _isAvailable;

        public FakeTranscriptionProvider(TranscriptionProvider provider, bool isAvailable)
        {
            Provider = provider;
            _isAvailable = isAvailable;
        }

        public string Id => "fake";
        public TranscriptionProvider Provider { get; }

        public bool CanHandle(TranscriptionModelSelection selection) => true;
        public bool IsAvailable(TranscriptionModelSelection selection, ProviderAvailabilityContext context) => _isAvailable;

        public Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(TranscriptionRequest request, ProviderAvailabilityContext context, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SubtitleCue>>([]);
    }

    private sealed class FakeLocalRuntime : ILocalModelRuntime
    {
        public string RuntimeId => "fake";
        public string? ResolveExecutablePath(ProviderAvailabilityContext context) => null;
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public string? GetOpenAiApiKey() => null;
        public void SaveOpenAiApiKey(string apiKey) { }
        public string? GetGoogleTranslateApiKey() => null;
        public void SaveGoogleTranslateApiKey(string apiKey) { }
        public string? GetDeepLApiKey() => null;
        public void SaveDeepLApiKey(string apiKey) { }
        public string? GetMicrosoftTranslatorApiKey() => null;
        public void SaveMicrosoftTranslatorApiKey(string apiKey) { }
        public string? GetMicrosoftTranslatorRegion() => null;
        public void SaveMicrosoftTranslatorRegion(string region) { }
        public string? GetSubtitleModelKey() => null;
        public void SaveSubtitleModelKey(string modelKey) { }
        public string? GetTranslationModelKey() => null;
        public void SaveTranslationModelKey(string modelKey) { }
        public void ClearTranslationModelKey() { }
        public bool GetAutoTranslateEnabled() => false;
        public void SaveAutoTranslateEnabled(bool enabled) { }
        public string? GetLlamaCppServerPath() => null;
        public void SaveLlamaCppServerPath(string path) { }
        public string? GetLlamaCppRuntimeVersion() => null;
        public void SaveLlamaCppRuntimeVersion(string version) { }
        public string? GetLlamaCppRuntimeSource() => null;
        public void SaveLlamaCppRuntimeSource(string source) { }
    }
}
