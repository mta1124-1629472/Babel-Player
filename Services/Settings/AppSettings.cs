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
    public const string ManagedGpuServiceUrl = "http://127.0.0.1:18000";

    /// <summary>Transcription provider identifier (e.g. "faster-whisper", "openai-whisper-api").</summary>
    public string TranscriptionProvider { get; set; } = ProviderNames.FasterWhisper;

    /// <summary>Public compute profile used for transcription: CPU, GPU, or cloud.</summary>
    public ComputeProfile TranscriptionProfile { get; set; } = ComputeProfile.Cpu;

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

    /// <summary>
    /// HuggingFace user access token required to download the gated pyannote speaker
    /// diarization model. Obtain one at https://huggingface.co/settings/tokens after
    /// accepting the model terms at https://hf.co/pyannote/speaker-diarization-3.1.
    /// Empty string = no token (will fail for gated models).
    /// </summary>
    public string DiarizationHuggingFaceToken { get; set; } = "";

    /// <summary>
    /// Optional lower bound on the number of speakers to detect.
    /// null means no constraint (pyannote auto-detects).
    /// </summary>
    public int? DiarizationMinSpeakers { get; set; } = null;

    /// <summary>
    /// Optional upper bound on the number of speakers to detect.
    /// null means no constraint (pyannote auto-detects).
    /// </summary>
    public int? DiarizationMaxSpeakers { get; set; } = null;

    /// <summary>Translation provider identifier (e.g. "google-translate-free", "openai").</summary>
    public string TranslationProvider { get; set; } = ProviderNames.GoogleTranslateFree;

    /// <summary>Public compute profile used for translation: CPU, GPU, or cloud.</summary>
    public ComputeProfile TranslationProfile { get; set; } = ComputeProfile.Cloud;

    /// <summary>Translation model name within the selected provider (e.g. "default", "gpt-4o").</summary>
    public string TranslationModel { get; set; } = "default";

    /// <summary>TTS provider identifier (e.g. "edge-tts", "elevenlabs").</summary>
    public string TtsProvider { get; set; } = ProviderNames.EdgeTts;

    /// <summary>Public compute profile used for TTS: CPU, GPU, or cloud.</summary>
    public ComputeProfile TtsProfile { get; set; } = ComputeProfile.Cloud;

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
    /// Preferred local GPU hosting backend when a stage uses the GPU compute profile.
    /// </summary>
    public GpuHostBackend PreferredLocalGpuBackend { get; set; } = GpuHostBackend.ManagedVenv;

    /// <summary>
    /// When true, the app will attempt to start the selected local GPU host
    /// during app startup even if no stage is currently set to the GPU profile.
    /// </summary>
    public bool AlwaysStartLocalGpuRuntimeAtAppStart { get; set; } = false;

    /// <summary>
    /// Base URL of the advanced Docker-hosted inference service used when
    /// <see cref="PreferredLocalGpuBackend"/> is <see cref="GpuHostBackend.DockerHost"/>.
    /// </summary>
    public string AdvancedGpuServiceUrl { get; set; } = "http://127.0.0.1:8000";

    [JsonIgnore]
    public InferenceRuntime TranscriptionRuntime
    {
        get => InferenceRuntimeCatalog.ResolveRuntime(TranscriptionProfile);
        set => TranscriptionProfile = InferenceRuntimeCatalog.MapLegacyRuntimeToProfile(value);
    }

    [JsonIgnore]
    public InferenceRuntime TranslationRuntime
    {
        get => InferenceRuntimeCatalog.ResolveRuntime(TranslationProfile);
        set => TranslationProfile = InferenceRuntimeCatalog.MapLegacyRuntimeToProfile(value);
    }

    [JsonIgnore]
    public InferenceRuntime TtsRuntime
    {
        get => InferenceRuntimeCatalog.ResolveRuntime(TtsProfile);
        set => TtsProfile = InferenceRuntimeCatalog.MapLegacyRuntimeToProfile(value);
    }

    [JsonIgnore]
    public string ContainerizedServiceUrl
    {
        get => AdvancedGpuServiceUrl;
        set => AdvancedGpuServiceUrl = value;
    }

    [JsonIgnore]
    public bool AlwaysRunContainerAtAppStart
    {
        get => AlwaysStartLocalGpuRuntimeAtAppStart;
        set => AlwaysStartLocalGpuRuntimeAtAppStart = value;
    }

    [JsonIgnore]
    public string EffectiveGpuServiceUrl
    {
        get
        {
            if (PreferredLocalGpuBackend == GpuHostBackend.ManagedVenv)
                return ManagedGpuServiceUrl;

            var overrideValue = Environment.GetEnvironmentVariable(InferenceServiceUrlEnvVar);
            return string.IsNullOrWhiteSpace(overrideValue)
                ? AdvancedGpuServiceUrl
                : overrideValue.Trim();
        }
    }

    [JsonIgnore]
    public string EffectiveContainerizedServiceUrl => EffectiveGpuServiceUrl;

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
    /// Requests an HDR-capable mpv output path for the OS/display pipeline.
    /// Pair with NVIDIA RTX HDR in NVIDIA Control Panel when the playback surface
    /// is one NVIDIA supports for driver-level SDR-to-HDR conversion.
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
