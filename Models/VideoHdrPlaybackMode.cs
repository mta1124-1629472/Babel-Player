namespace Babel.Player.Models;

/// <summary>
/// Mutually exclusive HDR playback strategies for the embedded mpv pipeline on Windows.
/// </summary>
public enum VideoHdrPlaybackMode
{
    /// <summary>No HDR handling requested from the app.</summary>
    Off = 0,

    /// <summary>
    /// NVIDIA Control Panel RTX Video / Auto HDR (SDR→HDR) path. The app avoids forcing mpv HDR
    /// output options so the driver can own conversion.
    /// </summary>
    NvidiaDriverRtxHdr = 1,

    /// <summary>
    /// mpv HDR passthrough (target-colorspace-hint, tone-mapping, etc.).
    /// </summary>
    MpvHdrPassthrough = 2,
}
