using System;
using System.Text.Json.Serialization;
using Babel.Player.Models;

namespace Babel.Player.Services.Settings;

/// <summary>
/// User-configurable application preferences. Serialised to app-settings.json.
/// </summary>
public sealed class AppSettings
{
    public const string InferenceServiceUrlEnvVar = "INFERENCE_SERVICE_URL";

    /// <summary>Transcription provider identifier (e.g. "faster-whisper", "openai-whisper-api").</summary>
    public string TranscriptionProvider { get; set; } = ProviderNames.FasterWhisper;

    /// <summary>Runtime host used for transcription: local, containerized, or cloud.</summary>
    public InferenceRuntime TranscriptionRuntime { get; set; } = InferenceRuntime.Local;

    /// <summary>Transcription model name within the selected provider (e.g. "base", "large-v3").</summary>
    public string TranscriptionModel { get; set; } = "base";

    /// <summary>
    /// CPU compute type used by transcription providers when running on CPU.
    /// Default "int8" matches current faster-whisper CPU behavior.
    /// </summary>
    public string TranscriptionCpuComputeType { get; set; } = "int8";

    /// <summary>
    /// CPU thread count hint for transcription providers.
    /// 0 means provider/runtime default auto-selection.
    /// </summary>
    public int TranscriptionCpuThreads { get; set; } = 0;

    /// <summary>
    /// Number of worker threads/processes used by transcription runtime internals.
    /// Keep conservative default to avoid oversubscription on low-core machines.
    /// </summary>
    public int TranscriptionNumWorkers { get; set; } = 1;

    /// <summary>Diarization provider identifier (e.g. "pyannote-local"). Empty string = diarization disabled.</summary>
    public string DiarizationProvider { get; set; } = "";

    /// <summary>Translation provider identifier (e.g. "google-translate-free", "openai").</summary>
    public string TranslationProvider { get; set; } = ProviderNames.GoogleTranslateFree;

    /// <summary>Runtime host used for translation: local, containerized, or cloud.</summary>
    public InferenceRuntime TranslationRuntime { get; set; } = InferenceRuntime.Cloud;

    /// <summary>Translation model name within the selected provider (e.g. "default", "gpt-4o").</summary>
    public string TranslationModel { get; set; } = "default";

    /// <summary>TTS provider identifier (e.g. "edge-tts", "elevenlabs").</summary>
    public string TtsProvider { get; set; } = ProviderNames.EdgeTts;

    /// <summary>Runtime host used for TTS: local, containerized, or cloud.</summary>
    public InferenceRuntime TtsRuntime { get; set; } = InferenceRuntime.Cloud;

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
    /// When true, the app will attempt to start the local containerized inference host
    /// during app startup even if no stage is currently set to the containerized runtime.
    /// Only applies when the effective service URL points at a local loopback address.
    /// </summary>
    public bool AlwaysRunContainerAtAppStart { get; set; } = false;

    [JsonIgnore]
    public string EffectiveContainerizedServiceUrl
    {
        get
        {
            var overrideValue = Environment.GetEnvironmentVariable(InferenceServiceUrlEnvVar);
            return string.IsNullOrWhiteSpace(overrideValue)
                ? ContainerizedServiceUrl
                : overrideValue.Trim();
        }
    }

    public void NormalizeLegacyInferenceSettings() =>
        InferenceRuntimeCatalog.NormalizeSettings(this);

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
    /// Use the gpu-next video output backend instead of the legacy gpu backend.
    /// Required for RTX Video Super Resolution and RTX HDR.
    /// Takes effect on app restart. Default false (opt-in).
    /// </summary>
    public bool VideoUseGpuNext { get; set; } = false;

    /// <summary>
    /// Enable NVIDIA RTX Video Super Resolution via the d3d11vpp filter.
    /// Requires gpu-next backend, RTX GPU, driver ≥ 551.23, and
    /// "RTX Video Enhancement" enabled in NVIDIA Control Panel.
    /// Takes effect on app restart.
    /// </summary>
    public bool VideoVsrEnabled { get; set; } = false;

    /// <summary>
    /// RTX Video Super Resolution quality level (1 = Performance … 4 = Quality).
    /// Only used when VideoVsrEnabled is true.
    /// Takes effect on app restart.
    /// </summary>
    public int VideoVsrQuality { get; set; } = 2;

    /// <summary>
    /// Enable the mpv HDR output pipeline (target-colorspace-hint + tone-mapping).
    /// Required for the OS/driver to receive a correct HDR signal.
    /// Pair with NVIDIA RTX HDR in NVIDIA Control Panel for AI SDR-to-HDR conversion.
    /// Requires an HDR-capable display with Windows HDR enabled.
    /// Takes effect on app restart.
    /// </summary>
    public bool VideoHdrEnabled { get; set; } = false;

    /// <summary>
    /// HDR tone-mapping algorithm used by mpv when VideoHdrEnabled is true.
    /// Options: bt.2390, mobius, clip, auto.
    /// Takes effect on app restart.
    /// </summary>
    public string VideoToneMapping { get; set; } = "bt.2390";

    /// <summary>
    /// Display peak brightness target in nits, or "auto".
    /// Used by mpv tone-mapping when VideoHdrEnabled is true.
    /// Takes effect on app restart.
    /// </summary>
    public string VideoTargetPeak { get; set; } = "auto";

    /// <summary>
    /// Enable dynamic per-frame peak detection for HDR tone-mapping.
    /// May cause brightness instability on some content. Default false.
    /// Takes effect on app restart.
    /// </summary>
    public bool VideoHdrComputePeak { get; set; } = false;

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
