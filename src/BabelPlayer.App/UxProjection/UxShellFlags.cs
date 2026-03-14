namespace BabelPlayer.App;

public sealed record UxShellFlags(
    bool IsOpenRequestInFlight,
    bool IsResumePromptVisible,
    bool IsStartupGateBlocking,
    bool IsSubtitlePanelOpen,
    bool IsSettingsPanelOpen,
    bool IsRuntimePromptVisible,
    bool IsCredentialPromptVisible,
    bool IsEndActionsVisible,
    string? PendingOpenMediaPath = null,
    string? ResumePromptMediaPath = null,
    TimeSpan? ResumePromptPosition = null,
    string? StartupGateMediaPath = null,
    string? EndedMediaPath = null,
    string? BannerMessage = null,
    string? BannerSeverity = null)
{
    public static UxShellFlags Default { get; } = new(
        IsOpenRequestInFlight: false,
        IsResumePromptVisible: false,
        IsStartupGateBlocking: false,
        IsSubtitlePanelOpen: false,
        IsSettingsPanelOpen: false,
        IsRuntimePromptVisible: false,
        IsCredentialPromptVisible: false,
        IsEndActionsVisible: false);
}
