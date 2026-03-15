namespace BabelPlayer.Core;

/// <summary>
/// Platform-agnostic contract for reading and writing user credentials and
/// AI provider configuration. Implementations must ensure that sensitive
/// values (API keys) are stored securely and are not readable by other users
/// or processes.
/// </summary>
public interface ICredentialStore
{
    // ── AI provider keys ────────────────────────────────────────────────────────

    string? GetOpenAiApiKey();
    void    SaveOpenAiApiKey(string apiKey);

    string? GetGoogleTranslateApiKey();
    void    SaveGoogleTranslateApiKey(string apiKey);

    string? GetDeepLApiKey();
    void    SaveDeepLApiKey(string apiKey);

    string? GetMicrosoftTranslatorApiKey();
    void    SaveMicrosoftTranslatorApiKey(string apiKey);

    string? GetMicrosoftTranslatorRegion();
    void    SaveMicrosoftTranslatorRegion(string region);

    // ── Model selection ───────────────────────────────────────────────────────────

    string? GetSubtitleModelKey();
    void    SaveSubtitleModelKey(string modelKey);

    string? GetTranslationModelKey();
    void    SaveTranslationModelKey(string modelKey);
    void    ClearTranslationModelKey();

    // ── Behaviour flags ──────────────────────────────────────────────────────────

    bool GetAutoTranslateEnabled();
    void SaveAutoTranslateEnabled(bool enabled);

    // ── llama.cpp runtime ─────────────────────────────────────────────────────────

    string? GetLlamaCppServerPath();
    void    SaveLlamaCppServerPath(string path);

    string? GetLlamaCppRuntimeVersion();
    void    SaveLlamaCppRuntimeVersion(string version);

    string? GetLlamaCppRuntimeSource();
    void    SaveLlamaCppRuntimeSource(string source);
}
