namespace Babel.Player.Services;

/// <summary>
/// Shared keys used for Qwen TTS speaker-reference mappings.
/// </summary>
public static class QwenReferenceKeys
{
    /// <summary>
    /// Default mapping key used for single-speaker Qwen runs when no per-speaker
    /// diarization mapping is configured yet.
    /// </summary>
    public const string SingleSpeakerDefault = "__qwen_single_speaker_default__";
}
