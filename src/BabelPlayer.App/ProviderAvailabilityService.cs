using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed record ProviderAvailabilityContext(
    CredentialFacade CredentialFacade,
    Func<string, string?> EnvironmentVariableReader);

public interface ITranscriptionProvider
{
    TranscriptionProvider Provider { get; }

    bool IsAvailable(TranscriptionModelSelection selection, ProviderAvailabilityContext context);
}

public interface ITranslationProvider
{
    TranslationProvider Provider { get; }

    bool IsConfigured(ProviderAvailabilityContext context);
}

public interface ILocalModelRuntime
{
    string RuntimeId { get; }

    string? ResolveExecutablePath(ProviderAvailabilityContext context);
}

public interface IProviderAvailabilityService
{
    string ResolvePersistedTranscriptionModelKey(string? modelKey);

    string? ResolvePersistedTranslationModelKey(string? modelKey);

    bool IsTranslationProviderConfigured(TranslationProvider provider);

    string? ResolveLlamaCppServerPath();
}

public sealed class ProviderAvailabilityService : IProviderAvailabilityService
{
    private readonly ProviderAvailabilityContext _context;
    private readonly IReadOnlyDictionary<TranscriptionProvider, ITranscriptionProvider> _transcriptionProviders;
    private readonly IReadOnlyDictionary<TranslationProvider, ITranslationProvider> _translationProviders;
    private readonly ILocalModelRuntime _localRuntime;

    public ProviderAvailabilityService(CredentialFacade credentialFacade, Func<string, string?> environmentVariableReader)
        : this(
            new ProviderAvailabilityContext(credentialFacade, environmentVariableReader),
            new ITranscriptionProvider[]
            {
                new LocalTranscriptionProvider(),
                new OpenAiTranscriptionProvider()
            },
            new ITranslationProvider[]
            {
                new OpenAiTranslationProviderAdapter(),
                new GoogleTranslationProviderAdapter(),
                new DeepLTranslationProviderAdapter(),
                new MicrosoftTranslationProviderAdapter(),
                new LocalLlamaTranslationProviderAdapter(TranslationProvider.LocalHyMt15_1_8B),
                new LocalLlamaTranslationProviderAdapter(TranslationProvider.LocalHyMt15_7B)
            },
            new LlamaCppRuntimeAdapter())
    {
    }

    internal ProviderAvailabilityService(
        ProviderAvailabilityContext context,
        IReadOnlyList<ITranscriptionProvider> transcriptionProviders,
        IReadOnlyList<ITranslationProvider> translationProviders,
        ILocalModelRuntime localRuntime)
    {
        _context = context;
        _transcriptionProviders = transcriptionProviders.ToDictionary(provider => provider.Provider);
        _translationProviders = translationProviders.ToDictionary(provider => provider.Provider);
        _localRuntime = localRuntime;
    }

    public string ResolvePersistedTranscriptionModelKey(string? modelKey)
    {
        var selection = SubtitleWorkflowCatalog.GetTranscriptionModel(modelKey);
        if (_transcriptionProviders.TryGetValue(selection.Provider, out var provider)
            && provider.IsAvailable(selection, _context))
        {
            return selection.Key;
        }

        return SubtitleWorkflowCatalog.DefaultTranscriptionModelKey;
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

        return _translationProviders.TryGetValue(provider, out var adapter)
               && adapter.IsConfigured(_context);
    }

    public string? ResolveLlamaCppServerPath()
    {
        return _localRuntime.ResolveExecutablePath(_context);
    }

    private static bool HasOpenAiApiKey(ProviderAvailabilityContext context)
    {
        return !string.IsNullOrWhiteSpace(context.EnvironmentVariableReader("OPENAI_API_KEY"))
               || !string.IsNullOrWhiteSpace(context.CredentialFacade.GetOpenAiApiKey());
    }

    private sealed class LocalTranscriptionProvider : ITranscriptionProvider
    {
        public TranscriptionProvider Provider => TranscriptionProvider.Local;

        public bool IsAvailable(TranscriptionModelSelection selection, ProviderAvailabilityContext context) => true;
    }

    private sealed class OpenAiTranscriptionProvider : ITranscriptionProvider
    {
        public TranscriptionProvider Provider => TranscriptionProvider.Cloud;

        public bool IsAvailable(TranscriptionModelSelection selection, ProviderAvailabilityContext context)
        {
            return HasOpenAiApiKey(context);
        }
    }

    private sealed class OpenAiTranslationProviderAdapter : ITranslationProvider
    {
        public TranslationProvider Provider => TranslationProvider.OpenAi;

        public bool IsConfigured(ProviderAvailabilityContext context) => HasOpenAiApiKey(context);
    }

    private sealed class GoogleTranslationProviderAdapter : ITranslationProvider
    {
        public TranslationProvider Provider => TranslationProvider.Google;

        public bool IsConfigured(ProviderAvailabilityContext context)
        {
            return !string.IsNullOrWhiteSpace(context.EnvironmentVariableReader("GOOGLE_TRANSLATE_API_KEY"))
                   || !string.IsNullOrWhiteSpace(context.EnvironmentVariableReader("GOOGLE_CLOUD_TRANSLATE_API_KEY"))
                   || !string.IsNullOrWhiteSpace(context.CredentialFacade.GetGoogleTranslateApiKey());
        }
    }

    private sealed class DeepLTranslationProviderAdapter : ITranslationProvider
    {
        public TranslationProvider Provider => TranslationProvider.DeepL;

        public bool IsConfigured(ProviderAvailabilityContext context)
        {
            return !string.IsNullOrWhiteSpace(context.EnvironmentVariableReader("DEEPL_API_KEY"))
                   || !string.IsNullOrWhiteSpace(context.CredentialFacade.GetDeepLApiKey());
        }
    }

    private sealed class MicrosoftTranslationProviderAdapter : ITranslationProvider
    {
        public TranslationProvider Provider => TranslationProvider.MicrosoftTranslator;

        public bool IsConfigured(ProviderAvailabilityContext context)
        {
            var apiKey = context.EnvironmentVariableReader("MICROSOFT_TRANSLATOR_API_KEY")
                         ?? context.EnvironmentVariableReader("AZURE_TRANSLATOR_KEY")
                         ?? context.CredentialFacade.GetMicrosoftTranslatorApiKey();
            var region = context.EnvironmentVariableReader("MICROSOFT_TRANSLATOR_REGION")
                         ?? context.EnvironmentVariableReader("AZURE_TRANSLATOR_REGION")
                         ?? context.CredentialFacade.GetMicrosoftTranslatorRegion();
            return !string.IsNullOrWhiteSpace(apiKey)
                   && !string.IsNullOrWhiteSpace(region);
        }
    }

    private sealed class LocalLlamaTranslationProviderAdapter : ITranslationProvider
    {
        public LocalLlamaTranslationProviderAdapter(TranslationProvider provider)
        {
            Provider = provider;
        }

        public TranslationProvider Provider { get; }

        public bool IsConfigured(ProviderAvailabilityContext context)
        {
            return new LlamaCppRuntimeAdapter().ResolveExecutablePath(context) is not null;
        }
    }

    private sealed class LlamaCppRuntimeAdapter : ILocalModelRuntime
    {
        public string RuntimeId => "llama.cpp";

        public string? ResolveExecutablePath(ProviderAvailabilityContext context)
        {
            var configuredPath = context.EnvironmentVariableReader("LLAMA_SERVER_PATH");
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            {
                return configuredPath;
            }

            configuredPath = context.CredentialFacade.GetLlamaCppServerPath();
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            {
                return configuredPath;
            }

            var installedPath = LlamaCppRuntimeInstaller.GetInstalledServerPath();
            if (File.Exists(installedPath))
            {
                return installedPath;
            }

            var pathValue = context.EnvironmentVariableReader("PATH");
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                return null;
            }

            foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var exePath = Path.Combine(segment, "llama-server.exe");
                if (File.Exists(exePath))
                {
                    return exePath;
                }

                var noExtensionPath = Path.Combine(segment, "llama-server");
                if (File.Exists(noExtensionPath))
                {
                    return noExtensionPath;
                }
            }

            return null;
        }
    }
}
