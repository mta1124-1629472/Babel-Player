using Babel.Player.Models;

namespace Babel.Player.Services.Settings;

/// <summary>
/// User-configurable application preferences. Serialised to app-settings.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Transcription provider identifier (e.g. "faster-whisper", "openai-whisper-api").</summary>
    public string TranscriptionProvider { get; set; } = ProviderNames.FasterWhisper;

    /// <summary>Transcription model name within the selected provider (e.g. "base", "large-v3").</summary>
    public string TranscriptionModel { get; set; } = "base";

    /// <summary>Translation provider identifier (e.g. "google-translate-free", "openai").</summary>
    public string TranslationProvider { get; set; } = ProviderNames.GoogleTranslateFree;

    /// <summary>Translation model name within the selected provider (e.g. "default", "gpt-4o").</summary>
    public string TranslationModel { get; set; } = "default";

    /// <summary>TTS provider identifier (e.g. "edge-tts", "elevenlabs").</summary>
    public string TtsProvider { get; set; } = ProviderNames.EdgeTts;

    /// <summary>
    /// TTS voice or model selection. For edge-tts this is an Edge-TTS voice name;
    /// for other providers it is the synthesis model name.
    /// </summary>
    public string TtsVoice { get; set; } = "en-US-AriaNeural";

    /// <summary>BCP-47 language code for the translation target.</summary>
    public string TargetLanguage { get; set; } = "en";

    /// <summary>
    /// Directory where Piper voice model (.onnx) files are stored.
    /// Empty string = use platform default (%LOCALAPPDATA%\piper\voices on Windows,
    /// ~/.local/share/piper/voices on Linux/macOS).
    /// </summary>
    public string PiperModelDir { get; set; } = "";

    /// <summary>
    /// Base URL of the containerized inference service (used when TranscriptionProvider,
    /// TranslationProvider, or TtsProvider is set to "containerized").
    /// </summary>
    public string ContainerizedServiceUrl { get; set; } = "http://localhost:8000";

    /// <summary>UI theme: "Light", "Dark", or "System".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Maximum entries kept in the recent-sessions list (1–20).</summary>
    public int MaxRecentSessions { get; set; } = 10;

    /// <summary>Whether to auto-save the session snapshot on app exit.</summary>
    public bool AutoSaveEnabled { get; set; } = true;
}
