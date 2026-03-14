namespace BabelPlayer.App;

public static class UxSessionProjector
{
    public static UxProjectionResult Project(UxProjectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var overlays = BuildOverlays(context.ShellFlags);
        var primary = BuildPrimaryState(context, overlays);
        return new UxProjectionResult(primary, overlays);
    }

    private static UxOverlayState BuildOverlays(UxShellFlags flags)
    {
        return new UxOverlayState(
            ResumePromptVisible: flags.IsResumePromptVisible,
            SubtitlePanelVisible: flags.IsSubtitlePanelOpen,
            SettingsPanelVisible: flags.IsSettingsPanelOpen,
            RuntimePromptVisible: flags.IsRuntimePromptVisible,
            CredentialPromptVisible: flags.IsCredentialPromptVisible,
            EndActionsVisible: flags.IsEndActionsVisible,
            BannerMessage: flags.BannerMessage,
            BannerSeverity: flags.BannerSeverity);
    }

    private static UxSessionState BuildPrimaryState(UxProjectionContext context, UxOverlayState overlays)
    {
        if (overlays.EndActionsVisible)
        {
            return UxSessionState.Ended;
        }

        if (context.ShellFlags.IsOpenRequestInFlight
            || overlays.ResumePromptVisible
            || context.ShellFlags.IsStartupGateBlocking)
        {
            return UxSessionState.Opening;
        }

        // A non-empty playback path is the current proxy for a watchable session.
        if (!string.IsNullOrWhiteSpace(context.Playback.Path))
        {
            return UxSessionState.Watching;
        }

        return UxSessionState.Idle;
    }
}
