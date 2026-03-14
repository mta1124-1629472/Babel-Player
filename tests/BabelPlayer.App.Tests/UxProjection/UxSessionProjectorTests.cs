using BabelPlayer.App;

namespace BabelPlayer.App.Tests.UxProjection;

public sealed class UxSessionProjectorTests
{
    [Fact]
    public void Project_ReturnsIdle_WhenNoMediaAndNoBlockingFlags()
    {
        var result = UxSessionProjector.Project(CreateContext());

        Assert.Equal(UxSessionState.Idle, result.Primary);
        Assert.False(result.Overlays.ResumePromptVisible);
        Assert.False(result.Overlays.EndActionsVisible);
        Assert.False(result.Overlays.BannerVisible);
    }

    [Fact]
    public void Project_ReturnsOpening_WhenOpenRequestIsInFlight()
    {
        var result = UxSessionProjector.Project(CreateContext(
            shellFlags: UxShellFlags.Default with
            {
                IsOpenRequestInFlight = true,
                PendingOpenMediaPath = "C:\\Media\\sample.mp4"
            }));

        Assert.Equal(UxSessionState.Opening, result.Primary);
    }

    [Fact]
    public void Project_ReturnsOpening_WhenResumePromptIsVisibleEvenIfPlaybackExists()
    {
        var result = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            shellFlags: UxShellFlags.Default with
            {
                IsResumePromptVisible = true,
                ResumePromptMediaPath = "C:\\Media\\sample.mp4",
                ResumePromptPosition = TimeSpan.FromMinutes(2)
            }));

        Assert.Equal(UxSessionState.Opening, result.Primary);
        Assert.True(result.Overlays.ResumePromptVisible);
    }

    [Fact]
    public void Project_ReturnsWatching_WhenCaptionGenerationContinuesWithoutStartupBlock()
    {
        var result = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            subtitleWorkflow: new SubtitleWorkflowSnapshot
            {
                CurrentVideoPath = "C:\\Media\\sample.mp4",
                IsCaptionGenerationInProgress = true,
                Cues = []
            }));

        Assert.Equal(UxSessionState.Watching, result.Primary);
    }

    [Fact]
    public void Project_ReturnsWatching_WhenTranslationWarmUpContinuesWithoutStartupBlock()
    {
        var result = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            subtitleWorkflow: new SubtitleWorkflowSnapshot
            {
                CurrentVideoPath = "C:\\Media\\sample.mp4",
                IsTranslationEnabled = true
            }));

        Assert.Equal(UxSessionState.Watching, result.Primary);
    }

    [Fact]
    public void Project_ReturnsOpening_OnlyWhileStartupGateBlocking()
    {
        var blockingResult = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            subtitleWorkflow: new SubtitleWorkflowSnapshot
            {
                CurrentVideoPath = "C:\\Media\\sample.mp4",
                IsCaptionGenerationInProgress = true
            },
            shellFlags: UxShellFlags.Default with
            {
                IsStartupGateBlocking = true,
                StartupGateMediaPath = "C:\\Media\\sample.mp4"
            }));

        var watchingResult = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            subtitleWorkflow: new SubtitleWorkflowSnapshot
            {
                CurrentVideoPath = "C:\\Media\\sample.mp4",
                IsCaptionGenerationInProgress = true
            }));

        Assert.Equal(UxSessionState.Opening, blockingResult.Primary);
        Assert.Equal(UxSessionState.Watching, watchingResult.Primary);
    }

    [Fact]
    public void Project_ReturnsEnded_WhenEndActionsVisibleEvenIfPlaybackExists()
    {
        var result = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            shellFlags: UxShellFlags.Default with
            {
                IsEndActionsVisible = true,
                EndedMediaPath = "C:\\Media\\sample.mp4"
            }));

        Assert.Equal(UxSessionState.Ended, result.Primary);
        Assert.True(result.Overlays.EndActionsVisible);
    }

    [Fact]
    public void Project_DoesNotLetPanelsAffectPrimaryState()
    {
        var result = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            shellFlags: UxShellFlags.Default with
            {
                IsSubtitlePanelOpen = true,
                IsSettingsPanelOpen = true
            }));

        Assert.Equal(UxSessionState.Watching, result.Primary);
        Assert.True(result.Overlays.SubtitlePanelVisible);
        Assert.True(result.Overlays.SettingsPanelVisible);
    }

    [Fact]
    public void Project_DoesNotDerivePrimaryStateFromBannerText()
    {
        var result = UxSessionProjector.Project(CreateContext(
            shellFlags: UxShellFlags.Default with
            {
                BannerMessage = "Preparing subtitles...",
                BannerSeverity = "info"
            }));

        Assert.Equal(UxSessionState.Idle, result.Primary);
        Assert.True(result.Overlays.BannerVisible);
        Assert.Equal("Preparing subtitles...", result.Overlays.BannerMessage);
        Assert.Equal("info", result.Overlays.BannerSeverity);
    }

    [Fact]
    public void Project_StaysWatching_WhenRuntimePromptVisibleAndMediaIsWatchable()
    {
        var result = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            shellFlags: UxShellFlags.Default with
            {
                IsRuntimePromptVisible = true
            }));

        Assert.Equal(UxSessionState.Watching, result.Primary);
        Assert.True(result.Overlays.RuntimePromptVisible);
    }

    [Fact]
    public void Project_PrioritizesEnded_WhenEndedAndOpeningConditionsOverlap()
    {
        var result = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            shellFlags: UxShellFlags.Default with
            {
                IsOpenRequestInFlight = true,
                IsResumePromptVisible = true,
                IsStartupGateBlocking = true,
                IsEndActionsVisible = true
            }));

        Assert.Equal(UxSessionState.Ended, result.Primary);
        Assert.True(result.Overlays.EndActionsVisible);
    }

    private static UxProjectionContext CreateContext(
        ShellPlaybackStateSnapshot? playback = null,
        ShellProjectionSnapshot? shellProjection = null,
        SubtitleWorkflowSnapshot? subtitleWorkflow = null,
        CredentialSetupSnapshot? credentialSetup = null,
        UxShellFlags? shellFlags = null)
    {
        return new UxProjectionContext(
            playback ?? new ShellPlaybackStateSnapshot(),
            shellProjection ?? new ShellProjectionSnapshot(),
            subtitleWorkflow ?? new SubtitleWorkflowSnapshot(),
            credentialSetup ?? new CredentialSetupSnapshot(),
            shellFlags ?? UxShellFlags.Default);
    }
}
