using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Windows implementation of <see cref="ISettingsStore"/>.
/// Sensitive values are encrypted with DPAPI (<see cref="ProtectedData"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecureSettingsStore : ISettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BabelPlayer");

    private static readonly string OpenAiApiKeyPath = Path.Combine(SettingsDirectory, "openai-api-key.bin");
    private static readonly string GoogleTranslateApiKeyPath = Path.Combine(SettingsDirectory, "google-translate-api-key.bin");
    private static readonly string DeepLApiKeyPath = Path.Combine(SettingsDirectory, "deepl-api-key.bin");
    private static readonly string MicrosoftTranslatorApiKeyPath = Path.Combine(SettingsDirectory, "microsoft-translator-api-key.bin");
    private static readonly string MicrosoftTranslatorRegionPath = Path.Combine(SettingsDirectory, "microsoft-translator-region.txt");
    private static readonly string LlamaCppServerPath = Path.Combine(SettingsDirectory, "llama-server-path.txt");
    private static readonly string LlamaCppRuntimeVersionPath = Path.Combine(SettingsDirectory, "llama-runtime-version.txt");
    private static readonly string LlamaCppRuntimeSourcePath = Path.Combine(SettingsDirectory, "llama-runtime-source.txt");
    private static readonly string SubtitleModelPath = Path.Combine(SettingsDirectory, "subtitle-model.txt");
    private static readonly string TranslationModelPath = Path.Combine(SettingsDirectory, "translation-model.txt");
    private static readonly string AutoTranslateEnabledPath = Path.Combine(SettingsDirectory, "auto-translate-enabled.txt");

    // ── ISettingsStore ───────────────────────────────────────────────────────

    public string GetAppDataDirectory()
    {
        Directory.CreateDirectory(SettingsDirectory);
        return SettingsDirectory;
    }

    public void SaveLlamaCppRuntimeSource(string source) =>
        SavePlaintextSetting(LlamaCppRuntimeSourcePath, source);

    public string? GetLlamaCppRuntimeSource() =>
        ReadPlaintextSetting(LlamaCppRuntimeSourcePath);

    // ── Windows-specific extras (direct call still fine inside App layer) ────

    public string? GetOpenAiApiKey()
    {
        if (!File.Exists(OpenAiApiKeyPath)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(OpenAiApiKeyPath);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var value = Encoding.UTF8.GetString(bytes).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch { return null; }
    }

    public void SaveOpenAiApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        Directory.CreateDirectory(SettingsDirectory);
        var bytes = Encoding.UTF8.GetBytes(apiKey.Trim());
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(OpenAiApiKeyPath, protectedBytes);
    }

    public string? GetGoogleTranslateApiKey() => ReadProtectedString(GoogleTranslateApiKeyPath);
    public void SaveGoogleTranslateApiKey(string apiKey) => SaveProtectedString(GoogleTranslateApiKeyPath, apiKey);

    public string? GetDeepLApiKey() => ReadProtectedString(DeepLApiKeyPath);
    public void SaveDeepLApiKey(string apiKey) => SaveProtectedString(DeepLApiKeyPath, apiKey);

    public string? GetMicrosoftTranslatorApiKey() => ReadProtectedString(MicrosoftTranslatorApiKeyPath);
    public void SaveMicrosoftTranslatorApiKey(string apiKey) => SaveProtectedString(MicrosoftTranslatorApiKeyPath, apiKey);

    public string? GetMicrosoftTranslatorRegion() => ReadPlaintextSetting(MicrosoftTranslatorRegionPath);
    public void SaveMicrosoftTranslatorRegion(string region) => SavePlaintextSetting(MicrosoftTranslatorRegionPath, region);

    public string? GetLlamaCppServerPath() => ReadPlaintextSetting(LlamaCppServerPath);
    public void SaveLlamaCppServerPath(string path) => SavePlaintextSetting(LlamaCppServerPath, path);

    public string? GetLlamaCppRuntimeVersion() => ReadPlaintextSetting(LlamaCppRuntimeVersionPath);
    public void SaveLlamaCppRuntimeVersion(string version) => SavePlaintextSetting(LlamaCppRuntimeVersionPath, version);

    public string? GetSubtitleModelKey() => ReadPlaintextSetting(SubtitleModelPath);
    public void SaveSubtitleModelKey(string modelKey) => SavePlaintextSetting(SubtitleModelPath, modelKey);

    public string? GetTranslationModelKey() => ReadPlaintextSetting(TranslationModelPath);
    public void SaveTranslationModelKey(string modelKey) => SavePlaintextSetting(TranslationModelPath, modelKey);
    public void ClearTranslationModelKey() => TryDeleteFile(TranslationModelPath);

    public bool GetAutoTranslateEnabled()
    {
        var value = ReadPlaintextSetting(AutoTranslateEnabledPath);
        return bool.TryParse(value, out var parsed) && parsed;
    }

    public void SaveAutoTranslateEnabled(bool enabled) =>
        SavePlaintextSetting(AutoTranslateEnabledPath, enabled ? "true" : "false");

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string? ReadPlaintextSetting(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var value = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch { return null; }
    }

    private static void SavePlaintextSetting(string path, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(path, value.Trim());
    }

    private static string? ReadProtectedString(string path)
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

    private static void SaveProtectedString(string path, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Directory.CreateDirectory(SettingsDirectory);
        var bytes = Encoding.UTF8.GetBytes(value.Trim());
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, protectedBytes);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
