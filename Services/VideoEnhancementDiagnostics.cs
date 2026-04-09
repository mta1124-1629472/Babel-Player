using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

internal enum VsrDiagnosticState
{
    NotEvaluated,
    Skipped,
    Applied,
    Rejected,
}

internal sealed record VsrDiagnosticSnapshot(
    VsrDiagnosticState State,
    string Trigger,
    bool UseGpuNextRequested,
    bool VsrRequested,
    int VsrQuality,
    string ResolvedPlan,
    string ReasonCode,
    string ReasonText,
    string? FilterChain,
    int VideoWidth,
    int VideoHeight,
    int DisplayWidth,
    int DisplayHeight,
    int MonitorWidth,
    int MonitorHeight,
    double Scale,
    string HwPixelFormat,
    int? BackendResultCode,
    string? BackendResultLabel,
    string? VideoOutput,
    string? GpuContext,
    string? HwdecCurrent)
{
    public string PlaybackStatusText =>
        State switch
        {
            VsrDiagnosticState.Applied => "VSR active",
            VsrDiagnosticState.Rejected => $"VSR rejected: {BackendResultLabel ?? ReasonText}",
            VsrDiagnosticState.Skipped => $"VSR skipped: {ReasonText}",
            _ => "VSR pending",
        };

    public string BackendSummary =>
        BackendResultCode is null
            ? "no backend command issued"
            : $"{BackendResultLabel ?? "backend result"} (result {BackendResultCode})";
}

internal sealed record VideoEnhancementDiagnostics(
    bool IsRtxCapableHint,
    bool IsVsrDriverSufficientHint,
    string? NvidiaDriverVersion,
    bool UseGpuNextRequested,
    bool VsrRequested,
    int VsrQuality,
    VsrDiagnosticSnapshot? LatestVsrSnapshot)
{
    public static VideoEnhancementDiagnostics Initial { get; } =
        new(
            IsRtxCapableHint: false,
            IsVsrDriverSufficientHint: false,
            NvidiaDriverVersion: null,
            UseGpuNextRequested: false,
            VsrRequested: false,
            VsrQuality: 2,
            LatestVsrSnapshot: null);

    public static VideoEnhancementDiagnostics Create(
        AppSettings settings,
        HardwareSnapshot hardwareSnapshot,
        VsrDiagnosticSnapshot? latestVsrSnapshot) =>
        new(
            IsRtxCapableHint: hardwareSnapshot.IsRtxCapable,
            IsVsrDriverSufficientHint: hardwareSnapshot.IsVsrDriverSufficient,
            NvidiaDriverVersion: hardwareSnapshot.NvidiaDriverVersion,
            UseGpuNextRequested: settings.VideoUseGpuNext,
            VsrRequested: settings.VideoVsrEnabled,
            VsrQuality: settings.VideoVsrQuality,
            LatestVsrSnapshot: latestVsrSnapshot);

    public string SupportHintText
    {
        get
        {
            if (LatestVsrSnapshot is { State: VsrDiagnosticState.Applied })
            {
                return "Support hint: the current playback path accepted the NVIDIA d3d11vpp VSR command.";
            }

            if (IsRtxCapableHint && IsVsrDriverSufficientHint)
            {
                return string.IsNullOrWhiteSpace(NvidiaDriverVersion)
                    ? "Support hint: RTX-class GPU detected and the driver satisfies the 551.23 VSR floor."
                    : $"Support hint: RTX-class GPU detected and NVIDIA driver {NvidiaDriverVersion} satisfies the 551.23 VSR floor.";
            }

            if (IsRtxCapableHint)
            {
                return string.IsNullOrWhiteSpace(NvidiaDriverVersion)
                    ? "Support hint: RTX-class GPU detected, but the NVIDIA driver floor for VSR has not been confirmed."
                    : $"Support hint: RTX-class GPU detected, but NVIDIA driver {NvidiaDriverVersion} does not satisfy the 551.23 VSR minimum.";
            }

            if (!string.IsNullOrWhiteSpace(NvidiaDriverVersion))
            {
                return $"Support hint: NVIDIA driver {NvidiaDriverVersion} is visible, but the current GPU was not identified as RTX-capable.";
            }

            return "Support hint: no RTX-capable NVIDIA VSR path has been confirmed yet.";
        }
    }

    public string RequestedStateText =>
        !UseGpuNextRequested
            ? "Requested state: gpu-next is disabled, so the d3d11vpp VSR path cannot be attempted."
            : !VsrRequested
                ? "Requested state: gpu-next is enabled, but RTX VSR itself is turned off."
                : $"Requested state: gpu-next is enabled and RTX VSR is requested (quality setting is not applied to the current mpv filter).";

    public string ResolvedStateText =>
        LatestVsrSnapshot is null
            ? VsrRequested && UseGpuNextRequested
                ? "Resolved state: waiting for the first playback-time VSR evaluation."
                : "Resolved state: no VSR evaluation has run for the current playback session."
            : LatestVsrSnapshot.State switch
            {
                VsrDiagnosticState.Applied => "Resolved state: the transport attempted VSR and libmpv accepted the filter.",
                VsrDiagnosticState.Rejected => "Resolved state: the transport attempted VSR, but libmpv rejected the filter command.",
                VsrDiagnosticState.Skipped => $"Resolved state: the transport skipped VSR because {LatestVsrSnapshot.ReasonText}.",
                _ => "Resolved state: VSR evaluation is pending.",
            };

    public string LastReasonText =>
        LatestVsrSnapshot is null
            ? "Last reason: no VSR skip, apply, or rejection has been recorded yet."
            : LatestVsrSnapshot.State switch
            {
                VsrDiagnosticState.Applied => "Last reason: libmpv accepted the requested VSR filter.",
                VsrDiagnosticState.Rejected => $"Last reason: {LatestVsrSnapshot.BackendResultLabel ?? LatestVsrSnapshot.ReasonText} (result {LatestVsrSnapshot.BackendResultCode}).",
                _ => $"Last reason: {LatestVsrSnapshot.ReasonText}.",
            };

    public string LastFilterText =>
        string.IsNullOrWhiteSpace(LatestVsrSnapshot?.FilterChain)
            ? "Last filter: none"
            : $"Last filter: {LatestVsrSnapshot.FilterChain}";

    public string PlaybackStatusText =>
        LatestVsrSnapshot?.PlaybackStatusText
        ?? (UseGpuNextRequested && VsrRequested ? "VSR pending" : string.Empty);

    public bool HasPlaybackStatus => !string.IsNullOrWhiteSpace(PlaybackStatusText);

    public string BackendSummaryText =>
        LatestVsrSnapshot?.BackendSummary ?? "Backend result: not attempted";
}
