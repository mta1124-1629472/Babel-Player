using System.Runtime.Versioning;

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
    string? GetLlamaCppServerPath();
    void SaveLlamaCppServerPath(string path);
    string? GetLlamaCppRuntimeVersion();
    void SaveLlamaCppRuntimeVersion(string version);
    string? GetLlamaCppRuntimeSource();
    void SaveLlamaCppRuntimeSource(string source);
}

/// <summary>
/// Windows implementation: delegates to <see cref="SecureSettingsStore"/> (DPAPI).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecureCredentialStore : ICredentialStore
{
    private readonly SecureSettingsStore _store;

    public SecureCredentialStore(SecureSettingsStore store)
    {
        _store = store;
    }

    public string? GetOpenAiApiKey()                              => _store.GetOpenAiApiKey();
    public void    SaveOpenAiApiKey(string apiKey)                => _store.SaveOpenAiApiKey(apiKey);
    public string? GetGoogleTranslateApiKey()                     => _store.GetGoogleTranslateApiKey();
    public void    SaveGoogleTranslateApiKey(string apiKey)       => _store.SaveGoogleTranslateApiKey(apiKey);
    public string? GetDeepLApiKey()                               => _store.GetDeepLApiKey();
    public void    SaveDeepLApiKey(string apiKey)                 => _store.SaveDeepLApiKey(apiKey);
    public string? GetMicrosoftTranslatorApiKey()                 => _store.GetMicrosoftTranslatorApiKey();
    public void    SaveMicrosoftTranslatorApiKey(string apiKey)   => _store.SaveMicrosoftTranslatorApiKey(apiKey);
    public string? GetMicrosoftTranslatorRegion()                 => _store.GetMicrosoftTranslatorRegion();
    public void    SaveMicrosoftTranslatorRegion(string region)   => _store.SaveMicrosoftTranslatorRegion(region);
    public string? GetSubtitleModelKey()                          => _store.GetSubtitleModelKey();
    public void    SaveSubtitleModelKey(string modelKey)          => _store.SaveSubtitleModelKey(modelKey);
    public string? GetTranslationModelKey()                       => _store.GetTranslationModelKey();
    public void    SaveTranslationModelKey(string modelKey)       => _store.SaveTranslationModelKey(modelKey);
    public void    ClearTranslationModelKey()                     => _store.ClearTranslationModelKey();
    public bool    GetAutoTranslateEnabled()                      => _store.GetAutoTranslateEnabled();
    public void    SaveAutoTranslateEnabled(bool enabled)         => _store.SaveAutoTranslateEnabled(enabled);
    public string? GetLlamaCppServerPath()                        => _store.GetLlamaCppServerPath();
    public void    SaveLlamaCppServerPath(string path)            => _store.SaveLlamaCppServerPath(path);
    public string? GetLlamaCppRuntimeVersion()                    => _store.GetLlamaCppRuntimeVersion();
    public void    SaveLlamaCppRuntimeVersion(string version)     => _store.SaveLlamaCppRuntimeVersion(version);
    public string? GetLlamaCppRuntimeSource()                     => _store.GetLlamaCppRuntimeSource();
    public void    SaveLlamaCppRuntimeSource(string source)       => _store.SaveLlamaCppRuntimeSource(source);
}

public sealed class CredentialFacade
{
    private readonly ICredentialStore _store;

    /// <summary>
    /// Default constructor — selects the correct store for the current OS.
    /// Windows: DPAPI-backed <see cref="SecureCredentialStore"/>.
    /// Linux/macOS: AES-256-GCM <see cref="XdgCredentialStore"/>.
    /// </summary>
    public CredentialFacade()
        : this(CreateDefaultStore())
    {
    }

    public CredentialFacade(ICredentialStore store)
    {
        _store = store;
    }

    private static ICredentialStore CreateDefaultStore()
    {
        if (OperatingSystem.IsWindows())
            return new SecureCredentialStore(new SecureSettingsStore());

        return new XdgCredentialStore();
    }

    public string? GetOpenAiApiKey()                              => _store.GetOpenAiApiKey();
    public void    SaveOpenAiApiKey(string apiKey)                => _store.SaveOpenAiApiKey(apiKey);
    public string? GetGoogleTranslateApiKey()                     => _store.GetGoogleTranslateApiKey();
    public void    SaveGoogleTranslateApiKey(string apiKey)       => _store.SaveGoogleTranslateApiKey(apiKey);
    public string? GetDeepLApiKey()                               => _store.GetDeepLApiKey();
    public void    SaveDeepLApiKey(string apiKey)                 => _store.SaveDeepLApiKey(apiKey);
    public string? GetMicrosoftTranslatorApiKey()                 => _store.GetMicrosoftTranslatorApiKey();
    public void    SaveMicrosoftTranslatorApiKey(string apiKey)   => _store.SaveMicrosoftTranslatorApiKey(apiKey);
    public string? GetMicrosoftTranslatorRegion()                 => _store.GetMicrosoftTranslatorRegion();
    public void    SaveMicrosoftTranslatorRegion(string region)   => _store.SaveMicrosoftTranslatorRegion(region);
    public string? GetSubtitleModelKey()                          => _store.GetSubtitleModelKey();
    public void    SaveSubtitleModelKey(string modelKey)          => _store.SaveSubtitleModelKey(modelKey);
    public string? GetTranslationModelKey()                       => _store.GetTranslationModelKey();
    public void    SaveTranslationModelKey(string modelKey)       => _store.SaveTranslationModelKey(modelKey);
    public void    ClearTranslationModelKey()                     => _store.ClearTranslationModelKey();
    public bool    GetAutoTranslateEnabled()                      => _store.GetAutoTranslateEnabled();
    public void    SaveAutoTranslateEnabled(bool enabled)         => _store.SaveAutoTranslateEnabled(enabled);
    public string? GetLlamaCppServerPath()                        => _store.GetLlamaCppServerPath();
    public void    SaveLlamaCppServerPath(string path)            => _store.SaveLlamaCppServerPath(path);
    public string? GetLlamaCppRuntimeVersion()                    => _store.GetLlamaCppRuntimeVersion();
    public void    SaveLlamaCppRuntimeVersion(string version)     => _store.SaveLlamaCppRuntimeVersion(version);
    public string? GetLlamaCppRuntimeSource()                     => _store.GetLlamaCppRuntimeSource();
    public void    SaveLlamaCppRuntimeSource(string source)       => _store.SaveLlamaCppRuntimeSource(source);
}
