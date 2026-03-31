using System.Collections.Generic;

namespace Babel.Player.Models;

/// <summary>
/// Static option lists for provider/model dropdowns throughout the app.
/// All option lists are stored here to avoid duplication across ViewModels and the Settings window.
/// </summary>
public static class ProviderOptions
{
    // ── Transcription ──────────────────────────────────────────────────────────

    public static IReadOnlyList<string> TranscriptionProviders { get; } =
        ["faster-whisper", "openai-whisper-api", "google-stt"];

    public static IReadOnlyList<string> GetTranscriptionModels(string provider) => provider switch
    {
        "faster-whisper"    => ["tiny", "base", "small", "medium", "large-v3"],
        "openai-whisper-api" => ["whisper-1", "gpt-4o-transcribe"],
        "google-stt"        => ["default"],
        _                   => ["default"],
    };

    // ── Translation ────────────────────────────────────────────────────────────

    public static IReadOnlyList<string> TranslationProviders { get; } =
        ["google-translate-free", "nllb-200", "deepl", "openai"];

    public static IReadOnlyList<string> GetTranslationModels(string provider) => provider switch
    {
        "google-translate-free" => ["default"],
        "nllb-200"              => ["nllb-200-distilled-600M", "nllb-200-distilled-1.3B", "nllb-200-1.3B"],
        "deepl"                 => ["default"],
        "openai"                => ["gpt-4o", "gpt-4o-mini"],
        _                       => ["default"],
    };

    // ── TTS ────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<string> TtsProviders { get; } =
        ["edge-tts", "piper", "elevenlabs", "google-cloud-tts", "openai-tts"];

    /// <summary>
    /// For edge-tts this returns the voice list (TtsVoice in AppSettings IS the "model").
    /// For piper this returns a curated list of voice names (each maps to a .onnx model file).
    /// For other providers it returns synthesis model names.
    /// </summary>
    public static IReadOnlyList<string> GetTtsOptions(string provider) => provider switch
    {
        "edge-tts"         => EdgeTtsVoices,
        "piper"            => PiperVoices,
        "elevenlabs"       => ["eleven_multilingual_v2", "eleven_turbo_v2_5", "eleven_flash_v2_5"],
        "google-cloud-tts" => ["standard", "wavenet", "neural2"],
        "openai-tts"       => ["tts-1", "tts-1-hd", "gpt-4o-mini-tts"],
        _                  => ["default"],
    };

    // ── Piper voice list ───────────────────────────────────────────────────────

    /// <summary>
    /// Curated Piper voice names. Each name corresponds to a .onnx model file
    /// that must be present in the configured Piper model directory.
    /// </summary>
    public static IReadOnlyList<string> PiperVoices { get; } =
    [
        "en_US-lessac-medium",
        "en_US-ryan-high",
        "en_US-ljspeech-high",
        "en_GB-alan-medium",
        "de_DE-thorsten-medium",
        "fr_FR-gilles-low",
        "es_ES-mls_10246-low",
    ];

    // ── Edge-TTS voice list ────────────────────────────────────────────────────

    public static IReadOnlyList<string> EdgeTtsVoices { get; } =
    [
        "en-US-AriaNeural",   "en-US-GuyNeural",    "en-US-JennyNeural",  "en-US-ChristopherNeural",
        "en-GB-SoniaNeural",  "en-GB-RyanNeural",   "en-AU-NatashaNeural","en-AU-WilliamNeural",
        "es-ES-ElviraNeural", "es-ES-AlvaroNeural", "fr-FR-DeniseNeural", "fr-FR-HenriNeural",
        "de-DE-KatjaNeural",  "de-DE-ConradNeural", "it-IT-ElsaNeural",   "it-IT-DiegoNeural",
        "pt-BR-FranciscaNeural","pt-BR-AntonioNeural","ja-JP-NanamiNeural","ja-JP-KeitaNeural",
        "ko-KR-SunHiNeural",  "ko-KR-InJoonNeural", "zh-CN-XiaoxiaoNeural","zh-CN-YunxiNeural",
        "ar-SA-ZariyahNeural","ar-SA-HamedNeural",  "hi-IN-SwaraNeural",  "hi-IN-MadhurNeural",
        "ru-RU-SvetlanaNeural","ru-RU-DmitryNeural",
    ];

    // ── Key requirements ───────────────────────────────────────────────────────

    /// <summary>True if this provider requires an API key stored in ApiKeyStore.</summary>
    public static bool RequiresApiKey(string provider) => provider switch
    {
        "faster-whisper"        => false,
        "google-translate-free" => false,
        "nllb-200"              => false,
        "edge-tts"              => false,
        "piper"                 => false,
        _                       => true,
    };

    /// <summary>
    /// Maps a provider ID to the credential-store key that holds its API key.
    /// Returns null if the provider needs no key.
    /// </summary>
    public static string? GetCredentialKey(string provider) => provider switch
    {
        "openai-whisper-api"   => "openai",
        "openai"               => "openai",
        "openai-tts"           => "openai",
        "google-stt"           => "google-ai",
        "google-cloud-tts"     => "google-ai",
        "deepl"                => "deepl",
        "elevenlabs"           => "elevenlabs",
        _                      => null,
    };
}
