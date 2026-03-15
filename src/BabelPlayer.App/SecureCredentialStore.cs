using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Windows credential store. Sensitive values (API keys) are encrypted with
/// DPAPI (<see cref="ProtectedData"/>, <see cref="DataProtectionScope.CurrentUser"/>).
/// Non-sensitive values (model keys, paths, flags) are stored as plain text.
/// Uses <c>%LOCALAPPDATA%\BabelPlayer</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecureCredentialStore : ICredentialStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BabelPlayer");

    // ── Sensitive (DPAPI encrypted) ──────────────────────────────────────────
    private static readonly string OpenAiApiKeyPath              = Path.Combine(SettingsDirectory, "openai-api-key.bin");
    private static readonly string GoogleTranslateApiKeyPath     = Path.Combine(SettingsDirectory, "google-translate-api-key.bin");
    private static readonly string DeepLApiKeyPath               = Path.Combine(SettingsDirectory, "deepl-api-key.bin");
    private static readonly string MicrosoftTranslatorApiKeyPath = Path.Combine(SettingsDirectory, "microsoft-translator-api-key.bin");

    // ── Non-sensitive (plain text) ────────────────────────────────────────────
    private static readonly string MicrosoftTranslatorRegionPath  = Path.Combine(SettingsDirectory, "microsoft-translator-region.txt");
    private static readonly string LlamaCppServerPath             = Path.Combine(SettingsDirectory, "llama-server-path.txt");
    private static readonly string LlamaCppRuntimeVersionPath     = Path.Combine(SettingsDirectory, "llama-runtime-version.txt");
    private static readonly string LlamaCppRuntimeSourcePath      = Path.Combine(SettingsDirectory, "llama-runtime-source.txt");
    private static readonly string SubtitleModelPath              = Path.Combine(SettingsDirectory, "subtitle-model.txt");
    private static readonly string TranslationModelPath           = Path.Combine(SettingsDirectory, "translation-model.txt");
    private static readonly string AutoTranslateEnabledPath       = Path.Combine(SettingsDirectory, "auto-translate-enabled.txt");

    // ── ICredentialStore ─────────────────────────────────────────────────────

    public string? GetOpenAiApiKey()               => ReadProtected(OpenAiApiKeyPath);
    public void    SaveOpenAiApiKey(string apiKey) => WriteProtected(OpenAiApiKeyPath, apiKey);

    public string? GetGoogleTranslateApiKey()               => ReadProtected(GoogleTranslateApiKeyPath);
    public void    SaveGoogleTranslateApiKey(string apiKey) => WriteProtected(GoogleTranslateApiKeyPath, apiKey);

    public string? GetDeepLApiKey()               => ReadProtected(DeepLApiKeyPath);
    public void    SaveDeepLApiKey(string apiKey) => WriteProtected(DeepLApiKeyPath, apiKey);

    public string? GetMicrosoftTranslatorApiKey()               => ReadProtected(MicrosoftTranslatorApiKeyPath);
    public void    SaveMicrosoftTranslatorApiKey(string apiKey) => WriteProtected(MicrosoftTranslatorApiKeyPath, apiKey);

    public string? GetMicrosoftTranslatorRegion()                => ReadPlaintext(MicrosoftTranslatorRegionPath);
    public void    SaveMicrosoftTranslatorRegion(string region)  => WritePlaintext(MicrosoftTranslatorRegionPath, region);

    public string? GetLlamaCppServerPath()              => ReadPlaintext(LlamaCppServerPath);
    public void    SaveLlamaCppServerPath(string path)  => WritePlaintext(LlamaCppServerPath, path);

    public string? GetLlamaCppRuntimeVersion()                 => ReadPlaintext(LlamaCppRuntimeVersionPath);
    public void    SaveLlamaCppRuntimeVersion(string version)  => WritePlaintext(LlamaCppRuntimeVersionPath, version);

    public string? GetLlamaCppRuntimeSource()                => ReadPlaintext(LlamaCppRuntimeSourcePath);
    public void    SaveLlamaCppRuntimeSource(string source)  => WritePlaintext(LlamaCppRuntimeSourcePath, source);

    public string? GetSubtitleModelKey()                   => ReadPlaintext(SubtitleModelPath);
    public void    SaveSubtitleModelKey(string modelKey)   => WritePlaintext(SubtitleModelPath, modelKey);

    public string? GetTranslationModelKey()                  => ReadPlaintext(TranslationModelPath);
    public void    SaveTranslationModelKey(string modelKey)  => WritePlaintext(TranslationModelPath, modelKey);
    public void    ClearTranslationModelKey()                => TryDelete(TranslationModelPath);

    public bool GetAutoTranslateEnabled()
    {
        var value = ReadPlaintext(AutoTranslateEnabledPath);
        return bool.TryParse(value, out var parsed) && parsed;
    }

    public void SaveAutoTranslateEnabled(bool enabled) =>
        WritePlaintext(AutoTranslateEnabledPath, enabled ? "true" : "false");

    // ── DPAPI helpers ─────────────────────────────────────────────────────────

    private static string? ReadProtected(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var value = Encoding.UTF8.GetString(bytes).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch { return null; }
    }

    private static void WriteProtected(string path, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Directory.CreateDirectory(SettingsDirectory);
        var bytes = Encoding.UTF8.GetBytes(value.Trim());
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, protectedBytes);
    }

    // ── Plaintext helpers ──────────────────────────────────────────────────────

    private static string? ReadPlaintext(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var value = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch { return null; }
    }

    private static void WritePlaintext(string path, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(path, value.Trim());
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
