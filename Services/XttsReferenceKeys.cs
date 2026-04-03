namespace Babel.Player.Services;

/// <summary>
/// Shared keys used for XTTS speaker-reference mappings.
/// </summary>
public static class XttsReferenceKeys
{
    /// <summary>
    /// Default mapping key used for single-speaker XTTS runs when no per-speaker
    /// diarization mapping is configured yet.
    /// </summary>
    public const string SingleSpeakerDefault = "__xtts_single_speaker_default__";
}
