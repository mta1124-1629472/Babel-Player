namespace BabelPlayer.App;

public sealed record UxOverlayState(
    bool ResumePromptVisible,
    bool SubtitlePanelVisible,
    bool SettingsPanelVisible,
    bool RuntimePromptVisible,
    bool CredentialPromptVisible,
    bool EndActionsVisible,
    string? BannerMessage,
    string? BannerSeverity)
{
    public bool BannerVisible => !string.IsNullOrWhiteSpace(BannerMessage);
}
