namespace Babel.Player.Services.Settings;

/// <summary>
/// User-configurable application preferences. Serialised to app-settings.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Edge-TTS voice identifier used for new TTS generation.</summary>
    public string TtsVoice { get; set; } = "en-US-AriaNeural";

    /// <summary>BCP-47 language code for the translation target.</summary>
    public string TargetLanguage { get; set; } = "en";

    /// <summary>UI theme: "Light", "Dark", or "System".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Maximum entries kept in the recent-sessions list (1–20).</summary>
    public int MaxRecentSessions { get; set; } = 10;

    /// <summary>Whether to auto-save the session snapshot on app exit.</summary>
    public bool AutoSaveEnabled { get; set; } = true;
}
