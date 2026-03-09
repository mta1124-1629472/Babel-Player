namespace BabelPlayer.App;

public interface ICredentialStore
{
    string? GetOpenAiApiKey();
    void SaveOpenAiApiKey(string apiKey);
    string? GetGoogleTranslateApiKey();
    void SaveGoogleTranslateApiKey(string apiKey);
    string? GetDeepLApiKey();
    void SaveDeepLApiKey(string apiKey);
    string? GetMicrosoftTranslatorApiKey();
    void SaveMicrosoftTranslatorApiKey(string apiKey);
    string? GetMicrosoftTranslatorRegion();
    void SaveMicrosoftTranslatorRegion(string region);
    string? GetSubtitleModelKey();
    void SaveSubtitleModelKey(string modelKey);
    string? GetTranslationModelKey();
    void SaveTranslationModelKey(string modelKey);
    void ClearTranslationModelKey();
    bool GetAutoTranslateEnabled();
    void SaveAutoTranslateEnabled(bool enabled);
}

public sealed class SecureCredentialStore : ICredentialStore
{
    public string? GetOpenAiApiKey() => SecureSettingsStore.GetOpenAiApiKey();
    public void SaveOpenAiApiKey(string apiKey) => SecureSettingsStore.SaveOpenAiApiKey(apiKey);
    public string? GetGoogleTranslateApiKey() => SecureSettingsStore.GetGoogleTranslateApiKey();
    public void SaveGoogleTranslateApiKey(string apiKey) => SecureSettingsStore.SaveGoogleTranslateApiKey(apiKey);
    public string? GetDeepLApiKey() => SecureSettingsStore.GetDeepLApiKey();
    public void SaveDeepLApiKey(string apiKey) => SecureSettingsStore.SaveDeepLApiKey(apiKey);
    public string? GetMicrosoftTranslatorApiKey() => SecureSettingsStore.GetMicrosoftTranslatorApiKey();
    public void SaveMicrosoftTranslatorApiKey(string apiKey) => SecureSettingsStore.SaveMicrosoftTranslatorApiKey(apiKey);
    public string? GetMicrosoftTranslatorRegion() => SecureSettingsStore.GetMicrosoftTranslatorRegion();
    public void SaveMicrosoftTranslatorRegion(string region) => SecureSettingsStore.SaveMicrosoftTranslatorRegion(region);
    public string? GetSubtitleModelKey() => SecureSettingsStore.GetSubtitleModelKey();
    public void SaveSubtitleModelKey(string modelKey) => SecureSettingsStore.SaveSubtitleModelKey(modelKey);
    public string? GetTranslationModelKey() => SecureSettingsStore.GetTranslationModelKey();
    public void SaveTranslationModelKey(string modelKey) => SecureSettingsStore.SaveTranslationModelKey(modelKey);
    public void ClearTranslationModelKey() => SecureSettingsStore.ClearTranslationModelKey();
    public bool GetAutoTranslateEnabled() => SecureSettingsStore.GetAutoTranslateEnabled();
    public void SaveAutoTranslateEnabled(bool enabled) => SecureSettingsStore.SaveAutoTranslateEnabled(enabled);
}

public sealed class CredentialFacade
{
    private readonly ICredentialStore _store;

    public CredentialFacade()
        : this(new SecureCredentialStore())
    {
    }

    public CredentialFacade(ICredentialStore store)
    {
        _store = store;
    }

    public string? GetOpenAiApiKey() => _store.GetOpenAiApiKey();
    public void SaveOpenAiApiKey(string apiKey) => _store.SaveOpenAiApiKey(apiKey);

    public string? GetGoogleTranslateApiKey() => _store.GetGoogleTranslateApiKey();
    public void SaveGoogleTranslateApiKey(string apiKey) => _store.SaveGoogleTranslateApiKey(apiKey);

    public string? GetDeepLApiKey() => _store.GetDeepLApiKey();
    public void SaveDeepLApiKey(string apiKey) => _store.SaveDeepLApiKey(apiKey);

    public string? GetMicrosoftTranslatorApiKey() => _store.GetMicrosoftTranslatorApiKey();
    public void SaveMicrosoftTranslatorApiKey(string apiKey) => _store.SaveMicrosoftTranslatorApiKey(apiKey);

    public string? GetMicrosoftTranslatorRegion() => _store.GetMicrosoftTranslatorRegion();
    public void SaveMicrosoftTranslatorRegion(string region) => _store.SaveMicrosoftTranslatorRegion(region);

    public string? GetSubtitleModelKey() => _store.GetSubtitleModelKey();
    public void SaveSubtitleModelKey(string modelKey) => _store.SaveSubtitleModelKey(modelKey);

    public string? GetTranslationModelKey() => _store.GetTranslationModelKey();
    public void SaveTranslationModelKey(string modelKey) => _store.SaveTranslationModelKey(modelKey);
    public void ClearTranslationModelKey() => _store.ClearTranslationModelKey();

    public bool GetAutoTranslateEnabled() => _store.GetAutoTranslateEnabled();
    public void SaveAutoTranslateEnabled(bool enabled) => _store.SaveAutoTranslateEnabled(enabled);
}
