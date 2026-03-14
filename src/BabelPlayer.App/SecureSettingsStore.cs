using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace BabelPlayer.App;

[SupportedOSPlatform("windows")]
public static class SecureSettingsStore
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

    public static string GetAppDataDirectory()
    {
        Directory.CreateDirectory(SettingsDirectory);
        return SettingsDirectory;
    }

    public static string? GetOpenAiApiKey()
    {
        if (!File.Exists(OpenAiApiKeyPath))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(OpenAiApiKeyPath);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var value = Encoding.UTF8.GetString(bytes).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveOpenAiApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        Directory.CreateDirectory(SettingsDirectory);
        var bytes = Encoding.UTF8.GetBytes(apiKey.Trim());
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(OpenAiApiKeyPath, protectedBytes);
    }

    public static string? GetGoogleTranslateApiKey()
    {
        return ReadProtectedString(GoogleTranslateApiKeyPath);
    }

    public static void SaveGoogleTranslateApiKey(string apiKey)
    {
        SaveProtectedString(GoogleTranslateApiKeyPath, apiKey);
    }

    public static string? GetDeepLApiKey()
    {
        return ReadProtectedString(DeepLApiKeyPath);
    }

    public static void SaveDeepLApiKey(string apiKey)
    {
        SaveProtectedString(DeepLApiKeyPath, apiKey);
    }

    public static string? GetMicrosoftTranslatorApiKey()
    {
        return ReadProtectedString(MicrosoftTranslatorApiKeyPath);
    }

    public static void SaveMicrosoftTranslatorApiKey(string apiKey)
    {
        SaveProtectedString(MicrosoftTranslatorApiKeyPath, apiKey);
    }

    public static string? GetMicrosoftTranslatorRegion()
    {
        return ReadPlaintextSetting(MicrosoftTranslatorRegionPath);
    }

    public static void SaveMicrosoftTranslatorRegion(string region)
    {
        SavePlaintextSetting(MicrosoftTranslatorRegionPath, region);
    }

    public static string? GetLlamaCppServerPath()
    {
        return ReadPlaintextSetting(LlamaCppServerPath);
    }

    public static void SaveLlamaCppServerPath(string path)
    {
        SavePlaintextSetting(LlamaCppServerPath, path);
    }

    public static string? GetLlamaCppRuntimeVersion()
    {
        return ReadPlaintextSetting(LlamaCppRuntimeVersionPath);
    }

    public static void SaveLlamaCppRuntimeVersion(string version)
    {
        SavePlaintextSetting(LlamaCppRuntimeVersionPath, version);
    }

    public static string? GetLlamaCppRuntimeSource()
    {
        return ReadPlaintextSetting(LlamaCppRuntimeSourcePath);
    }

    public static void SaveLlamaCppRuntimeSource(string source)
    {
        SavePlaintextSetting(LlamaCppRuntimeSourcePath, source);
    }

    public static string? GetSubtitleModelKey()
    {
        return ReadPlaintextSetting(SubtitleModelPath);
    }

    public static void SaveSubtitleModelKey(string modelKey)
    {
        SavePlaintextSetting(SubtitleModelPath, modelKey);
    }

    public static string? GetTranslationModelKey()
    {
        return ReadPlaintextSetting(TranslationModelPath);
    }

    public static void SaveTranslationModelKey(string modelKey)
    {
        SavePlaintextSetting(TranslationModelPath, modelKey);
    }

    public static void ClearTranslationModelKey()
    {
        TryDeleteFile(TranslationModelPath);
    }

    public static bool GetAutoTranslateEnabled()
    {
        var value = ReadPlaintextSetting(AutoTranslateEnabledPath);
        return bool.TryParse(value, out var parsed) && parsed;
    }

    public static void SaveAutoTranslateEnabled(bool enabled)
    {
        SavePlaintextSetting(AutoTranslateEnabledPath, enabled ? "true" : "false");
    }

    private static string? ReadPlaintextSetting(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var value = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static void SavePlaintextSetting(string path, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(path, value.Trim());
    }

    private static string? ReadProtectedString(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var value = Encoding.UTF8.GetString(bytes).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
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
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
