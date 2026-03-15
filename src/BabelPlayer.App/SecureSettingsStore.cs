using System.IO;
using System.Runtime.Versioning;
using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Windows implementation of <see cref="ISettingsStore"/>.
/// Uses <c>%LOCALAPPDATA%\BabelPlayer</c>.
/// Sensitive values (API keys) are stored via <see cref="SecureCredentialStore"/> (DPAPI).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecureSettingsStore : ISettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BabelPlayer");

    private static readonly string LlamaCppRuntimeSourcePath =
        Path.Combine(SettingsDirectory, "llama-runtime-source.txt");

    // ── ISettingsStore ─────────────────────────────────────────────────────

    public string GetAppDataDirectory()
    {
        Directory.CreateDirectory(SettingsDirectory);
        return SettingsDirectory;
    }

    public string? GetLlamaCppRuntimeSource() =>
        ReadPlaintext(LlamaCppRuntimeSourcePath);

    public void SaveLlamaCppRuntimeSource(string source) =>
        SavePlaintext(LlamaCppRuntimeSourcePath, source);

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static void SavePlaintext(string path, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(path, value.Trim());
    }
}
