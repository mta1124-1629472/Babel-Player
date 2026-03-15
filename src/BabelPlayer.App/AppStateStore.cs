using System.IO;
using System.Text.Json;

namespace BabelPlayer.App;

public static class AppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string AppDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer");

    private static string SettingsPath => Path.Combine(AppDataDirectory, "player-settings.json");
    private static string ResumePath   => Path.Combine(AppDataDirectory, "playback-state.json");

    public static AppPlayerSettings LoadSettings()
    {
        return ReadJson(SettingsPath, new AppPlayerSettings());
    }

    public static void SaveSettings(AppPlayerSettings settings)
    {
        WriteJson(SettingsPath, settings);
    }

    public static IReadOnlyList<PlaybackResumeEntry> LoadResumeEntries()
    {
        return ReadJson(ResumePath, Array.Empty<PlaybackResumeEntry>()) ?? Array.Empty<PlaybackResumeEntry>();
    }

    public static void SaveResumeEntries(IReadOnlyList<PlaybackResumeEntry> entries)
    {
        WriteJson(ResumePath, entries);
    }

    private static T ReadJson<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path))
                return fallback;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }
}
