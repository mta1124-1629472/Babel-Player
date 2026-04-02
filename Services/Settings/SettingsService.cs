using System;
using System.IO;
using System.Text.Json;

namespace Babel.Player.Services.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to a JSON file.
/// Never throws — missing or corrupt files fall back to defaults silently.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly AppLog _log;

    public SettingsService(string filePath, AppLog log)
    {
        _filePath = filePath;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Returns saved settings, or a new <see cref="AppSettings"/> with defaults if the file
    /// is absent, empty, or unreadable.
    /// </summary>
    public AppSettings LoadOrDefault()
    {
        if (!File.Exists(_filePath))
        {
            var defaults = new AppSettings();
            defaults.NormalizeLegacyInferenceSettings();
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                var defaults = new AppSettings();
                defaults.NormalizeLegacyInferenceSettings();
                return defaults;
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                ?? new AppSettings();
            settings.NormalizeLegacyInferenceSettings();
            return settings;
        }
        catch (Exception ex)
        {
            _log.Warning($"Settings load failed ({ex.Message}). Using defaults.");
            var defaults = new AppSettings();
            defaults.NormalizeLegacyInferenceSettings();
            return defaults;
        }
    }

    /// <summary>Save settings. Failures are logged but non-fatal.</summary>
    public void Save(AppSettings settings)
    {
        try
        {
            settings.NormalizeLegacyInferenceSettings();
            File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, SerializerOptions));
        }
        catch (Exception ex)
        {
            _log.Error("Failed to save app settings.", ex);
        }
    }
}
