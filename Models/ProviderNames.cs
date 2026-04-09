namespace Babel.Player.Models;

/// <summary>
/// String constants for all provider identifiers used across the pipeline.
/// All provider string comparisons and switch expressions must reference these
/// constants rather than inline literals to prevent typos and enable safe renaming.
/// </summary>
public static class ProviderNames
{
    // ── Containerized inference service (all stages) ─────────────────────────
    public const string ContainerizedService = "containerized";

    // ── Transcription ───────────────────────────────────────────────────────
    public const string FasterWhisper         = "faster-whisper";
    public const string OpenAiWhisperApi       = "openai-whisper-api";
    public const string GoogleStt             = "google-stt";
    public const string GeminiTranscription   = "gemini-transcription";

    // ── Translation ──────────────────────────────────────────────────────
    public const string GoogleTranslateFree   = "google-translate-free";
    public const string Nllb200               = "nllb-200";
    public const string CTranslate2           = "ctranslate2";
    public const string Deepl                 = "deepl";
    public const string OpenAi                = "openai";
    public const string GeminiTranslation     = "gemini-translation";

    // ── TTS ──────────────────────────────────────────────────────────────
    public const string EdgeTts        = "edge-tts";
    public const string Piper          = "piper";
    public const string ElevenLabs     = "elevenlabs";
    public const string GoogleCloudTts = "google-cloud-tts";
    public const string OpenAiTts      = "openai-tts";
    public const string Qwen           = "qwen-tts";

    // ── Diarization ─────────────────────────────────────────────────────
    public const string NemoDiarizationAlias = "nemo";
    public const string WeSpeakerDiarizationAlias = "wespeaker";
    public const string NemoLocal = "nemo-local";
    public const string WeSpeakerLocal = "wespeaker-local";
}

/// <summary>
/// String constants for the credential-store keys used by API-key-required providers.
/// These must match the keys written and read by <see cref="Services.Credentials.ApiKeyStore"/>.
/// </summary>
public static class CredentialKeys
{
    public const string OpenAi        = "openai";
    public const string GoogleAi      = "google-ai";      // Google STT / Google Cloud TTS
    public const string GoogleGemini  = "google-gemini";  // Gemini transcription + translation
    public const string ElevenLabs    = "elevenlabs";
    public const string Deepl         = "deepl";
    public const string LegacyHuggingFace = "huggingface";
}
