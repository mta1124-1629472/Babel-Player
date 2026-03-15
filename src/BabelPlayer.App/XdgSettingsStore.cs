using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Linux/macOS settings store. Uses <c>~/.config/BabelPlayer</c> (XDG_CONFIG_HOME).
/// Plain-text only — no DPAPI equivalent on non-Windows platforms.
/// API keys should be stored via the system keyring (secret-tool / libsecret) in a future iteration.
/// </summary>
public sealed class XdgSettingsStore : ISettingsStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BabelPlayer");

    private static readonly string LlamaCppRuntimeSourcePath =
        Path.Combine(ConfigDir, "llama-runtime-source.txt");

    public string GetAppDataDirectory()
    {
        Directory.CreateDirectory(ConfigDir);
        return ConfigDir;
    }

    public void SaveLlamaCppRuntimeSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(LlamaCppRuntimeSourcePath, source.Trim());
    }

    public string? GetLlamaCppRuntimeSource()
    {
        if (!File.Exists(LlamaCppRuntimeSourcePath))
            return null;

        try
        {
            var value = File.ReadAllText(LlamaCppRuntimeSourcePath).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }
}
