using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PlayerApp.UI;

internal static class SecureSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlayerApp");

    private static readonly string OpenAiApiKeyPath = Path.Combine(SettingsDirectory, "openai-api-key.bin");

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
}
