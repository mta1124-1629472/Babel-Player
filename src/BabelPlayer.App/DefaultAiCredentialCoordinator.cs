using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class DefaultAiCredentialCoordinator : IAiCredentialCoordinator
{
    private readonly ICredentialStore _credentialStore;
    private readonly ICredentialDialogService? _credentialDialogService;
    private readonly Func<string, string?> _environmentVariableReader;
    private readonly Func<string, CancellationToken, Task> _validateOpenAiApiKeyAsync;
    private readonly Func<CloudTranslationOptions, CancellationToken, Task> _validateTranslationProviderAsync;

    public DefaultAiCredentialCoordinator(
        ICredentialStore credentialStore,
        ICredentialDialogService? credentialDialogService,
        Func<string, string?> environmentVariableReader,
        Func<string, CancellationToken, Task> validateOpenAiApiKeyAsync,
        Func<CloudTranslationOptions, CancellationToken, Task> validateTranslationProviderAsync)
    {
        _credentialStore = credentialStore;
        _credentialDialogService = credentialDialogService;
        _environmentVariableReader = environmentVariableReader;
        _validateOpenAiApiKeyAsync = validateOpenAiApiKeyAsync;
        _validateTranslationProviderAsync = validateTranslationProviderAsync;
    }

    public async Task<bool> EnsureOpenAiApiKeyAsync(CancellationToken cancellationToken)
    {
        var apiKey = _environmentVariableReader("OPENAI_API_KEY") ?? _credentialStore.GetOpenAiApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await _validateOpenAiApiKeyAsync(apiKey, cancellationToken);
            return true;
        }

        if (_credentialDialogService is null)
            return false;

        var submitted = await _credentialDialogService.PromptForApiKeyAsync(
            "OpenAI API Key",
            "Enter the OpenAI API key for cloud transcription and translation.",
            "Save",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(submitted))
            return false;

        await _validateOpenAiApiKeyAsync(submitted, cancellationToken);
        _credentialStore.SaveOpenAiApiKey(submitted.Trim());
        return true;
    }

    public async Task<bool> EnsureTranslationProviderCredentialsAsync(TranslationProvider provider, CancellationToken cancellationToken)
    {
        return provider switch
        {
            TranslationProvider.None => false,
            TranslationProvider.OpenAi => await EnsureOpenAiApiKeyAsync(cancellationToken),
            TranslationProvider.Google => await EnsureSingleApiKeyProviderAsync(
                "Google Translate API Key",
                "Enter the Google Translate API key.",
                apiKey => _credentialStore.SaveGoogleTranslateApiKey(apiKey),
                apiKey => new CloudTranslationOptions(CloudTranslationProvider.Google, apiKey),
                _environmentVariableReader("GOOGLE_TRANSLATE_API_KEY")
                    ?? _environmentVariableReader("GOOGLE_CLOUD_TRANSLATE_API_KEY")
                    ?? _credentialStore.GetGoogleTranslateApiKey(),
                cancellationToken),
            TranslationProvider.DeepL => await EnsureSingleApiKeyProviderAsync(
                "DeepL API Key",
                "Enter the DeepL API key.",
                apiKey => _credentialStore.SaveDeepLApiKey(apiKey),
                apiKey => new CloudTranslationOptions(CloudTranslationProvider.DeepL, apiKey),
                _environmentVariableReader("DEEPL_API_KEY") ?? _credentialStore.GetDeepLApiKey(),
                cancellationToken),
            TranslationProvider.MicrosoftTranslator => await EnsureMicrosoftCredentialsAsync(cancellationToken),
            TranslationProvider.LocalHyMt15_1_8B or TranslationProvider.LocalHyMt15_7B => true,
            _ => false
        };
    }

    private async Task<bool> EnsureSingleApiKeyProviderAsync(
        string title,
        string message,
        Action<string> persist,
        Func<string, CloudTranslationOptions> buildOptions,
        string? existingApiKey,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(existingApiKey))
        {
            await _validateTranslationProviderAsync(buildOptions(existingApiKey.Trim()), cancellationToken);
            return true;
        }

        if (_credentialDialogService is null)
            return false;

        var submitted = await _credentialDialogService.PromptForApiKeyAsync(title, message, "Save", cancellationToken);
        if (string.IsNullOrWhiteSpace(submitted))
            return false;

        await _validateTranslationProviderAsync(buildOptions(submitted.Trim()), cancellationToken);
        persist(submitted.Trim());
        return true;
    }

    private async Task<bool> EnsureMicrosoftCredentialsAsync(CancellationToken cancellationToken)
    {
        var apiKey = _environmentVariableReader("MICROSOFT_TRANSLATOR_API_KEY")
                     ?? _environmentVariableReader("AZURE_TRANSLATOR_KEY")
                     ?? _credentialStore.GetMicrosoftTranslatorApiKey();
        var region = _environmentVariableReader("MICROSOFT_TRANSLATOR_REGION")
                     ?? _environmentVariableReader("AZURE_TRANSLATOR_REGION")
                     ?? _credentialStore.GetMicrosoftTranslatorRegion();

        if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(region))
        {
            await _validateTranslationProviderAsync(
                new CloudTranslationOptions(CloudTranslationProvider.MicrosoftTranslator, apiKey.Trim(), null, region.Trim()),
                cancellationToken);
            return true;
        }

        if (_credentialDialogService is null)
            return false;

        var submitted = await _credentialDialogService.PromptForApiKeyWithRegionAsync(
            "Microsoft Translator",
            "Enter the Microsoft Translator API key and region.",
            "Save",
            cancellationToken);
        if (submitted is null || string.IsNullOrWhiteSpace(submitted.Value.ApiKey) || string.IsNullOrWhiteSpace(submitted.Value.Region))
            return false;

        await _validateTranslationProviderAsync(
            new CloudTranslationOptions(CloudTranslationProvider.MicrosoftTranslator, submitted.Value.ApiKey.Trim(), null, submitted.Value.Region.Trim()),
            cancellationToken);
        _credentialStore.SaveMicrosoftTranslatorApiKey(submitted.Value.ApiKey.Trim());
        _credentialStore.SaveMicrosoftTranslatorRegion(submitted.Value.Region.Trim());
        return true;
    }
}
