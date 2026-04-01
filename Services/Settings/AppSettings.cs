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

    /// <summary>
    /// libmpv hardware video decode method.
    /// "auto" lets mpv pick the best available decoder for the current GPU.
    /// Options: auto, auto-safe, no, d3d11va, d3d11va-copy, nvdec, nvdec-copy, qsv, dxva2.
    /// Takes effect on app restart.
    /// </summary>
    public string VideoHwdec { get; set; } = "auto";

    /// <summary>
    /// libmpv GPU rendering API.
    /// "auto" lets mpv pick (d3d11 on Windows, opengl elsewhere).
    /// Options: auto, d3d11, vulkan, opengl.
    /// Takes effect on app restart.
    /// </summary>
    public string VideoGpuApi { get; set; } = "auto";

    /// <summary>
    /// ffmpeg encoder used by the video export stage (not yet implemented).
    /// "auto" = resolve at export time from hardware detection.
    /// Options: auto, h264_nvenc, hevc_nvenc, h264_amf, hevc_amf, h264_qsv, hevc_qsv, libx264, libx265.
    /// </summary>
    public string VideoExportEncoder { get; set; } = "auto";

    /// <summary>UI theme: "Light", "Dark", or "System".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Maximum entries kept in the recent-sessions list (1–20).</summary>
    public int MaxRecentSessions { get; set; } = 10;

    /// <summary>Whether to auto-save the session snapshot on app exit.</summary>
    public bool AutoSaveEnabled { get; set; } = true;
}
