namespace Babel.Player.Models;

/// <summary>
/// Init-time libmpv video rendering options captured once at transport construction.
/// These options are passed to mpv_set_option_string before mpv_initialize() and cannot
/// be changed at runtime — changing them requires an app restart.
/// </summary>
public sealed record VideoPlaybackOptions(
    string HwdecMode       = "auto",
    string GpuApi          = "auto",
    bool   UseGpuNext      = false,
    bool   VsrEnabled      = false,
    bool   HdrEnabled      = false,
    bool   AllowHdrPassthrough = false,
    string ToneMapping     = "bt.2390",
    string TargetPeak      = "auto",
    bool   HdrComputePeak  = true);
