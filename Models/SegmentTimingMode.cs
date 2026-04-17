namespace Babel.Player.Models;

/// <summary>
/// Controls how dubbed TTS audio is time-aligned with the original segment window.
/// </summary>
public enum SegmentTimingMode
{
    /// <summary>
    /// No timing adjustment. TTS plays as-is; the source video drives segment transitions
    /// at the original segment boundary regardless of TTS clip length.
    /// </summary>
    Off,

    /// <summary>
    /// Time-stretch the TTS clip to exactly fill the original segment duration using ffmpeg
    /// atempo. Ratios outside [0.75, 1.75] are skipped.
    /// </summary>
    Stretch,

    /// <summary>
    /// Preview-only behavior: pause the source video while TTS plays, then seek to the
    /// segment end and resume. This mode is not applied to rendered dub/export output.
    /// </summary>
    Pause,
}
