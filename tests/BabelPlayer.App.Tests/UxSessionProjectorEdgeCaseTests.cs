using BabelPlayer.App;

namespace BabelPlayer.App.Tests.UxProjection;

public sealed class UxSessionProjectorEdgeCaseTests
{
    // ── State priority under simultaneous flags ────────────────────────────────

    [Fact]
    public void Project_PrioritizesEnded_WhenEndedAndWatchingConditionsSimultaneous()
    {
        var result = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            shellFlags: UxShellFlags.Default with
            {
                IsEndActionsVisible = true,
                EndedMediaPath = "C:\\Media\\sample.mp4",
                IsSubtitlePanelOpen = true
            }));

        Assert.Equal(UxSessionState.Ended, result.Primary);
        Assert.True(result.Overlays.EndActionsVisible);
    }

    [Fact]
    public void Project_PrioritizesOpening_WhenResumePromptAndWatchingConditionsOverlap()
    {
        var result = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            shellFlags: UxShellFlags.Default with
            {
                IsResumePromptVisible = true,
                ResumePromptMediaPath = "C:\\Media\\sample.mp4",
                ResumePromptPosition = TimeSpan.FromMinutes(1)
            },
            subtitleWorkflow: new SubtitleWorkflowSnapshot
            {
                CurrentVideoPath = "C:\\Media\\sample.mp4",
                IsCaptionGenerationInProgress = true,
                Cues = []
            }));

        // Resume prompt is a blocking startup gate → Opening wins over Watching
        Assert.Equal(UxSessionState.Opening, result.Primary);
    }

    // ── Overlay independence ───────────────────────────────────────────────────

    [Fact]
    public void Project_SettingsPanelVisible_WhenFlagIsSetAndMediaIsWatching()
    {
        var result = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            shellFlags: UxShellFlags.Default with
            {
                IsSettingsPanelOpen = true
            }));

        Assert.Equal(UxSessionState.Watching, result.Primary);
        Assert.True(result.Overlays.SettingsPanelVisible);
    }

    [Fact]
    public void Project_BothPanelsCanBeVisible_Simultaneously()
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

    // ── Idle is truly idle ────────────────────────────────────────────────────

    [Fact]
    public void Project_ReturnsIdle_WhenAllFlagsAreDefault()
    {
        var result = UxSessionProjector.Project(CreateContext());

        Assert.Equal(UxSessionState.Idle, result.Primary);
        Assert.False(result.Overlays.ResumePromptVisible);
        Assert.False(result.Overlays.EndActionsVisible);
        Assert.False(result.Overlays.SubtitlePanelVisible);
        Assert.False(result.Overlays.SettingsPanelVisible);
        Assert.False(result.Overlays.RuntimePromptVisible);
    }

    // ── Banner is independent of state ────────────────────────────────────────

    [Fact]
    public void Project_BannerVisibleDuringWatching()
    {
        var result = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            shellFlags: UxShellFlags.Default with
            {
                BannerMessage = "Subtitle loading failed.",
                BannerSeverity = "error"
            }));

        Assert.Equal(UxSessionState.Watching, result.Primary);
        Assert.True(result.Overlays.BannerVisible);
        Assert.Equal("error", result.Overlays.BannerSeverity);
    }

    [Fact]
    public void Project_BannerNotVisible_WhenMessageIsEmpty()
    {
        var result = UxSessionProjector.Project(CreateContext(
            shellFlags: UxShellFlags.Default with
            {
                BannerMessage = null
            }));

        Assert.False(result.Overlays.BannerVisible);
    }

    // ── Watching with subtitle generation and startup gate ───────────────────

    [Fact]
    public void Project_ReturnsOpening_WhenStartupGateIsBlocking()
    {
        var result = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\sample.mp4" },
            shellFlags: UxShellFlags.Default with
            {
                IsStartupGateBlocking = true
            }));

        Assert.Equal(UxSessionState.Opening, result.Primary);
    }

    [Fact]
    public void Project_ReturnsWatching_WhenStartupGateIsNotBlocking()
    {
        var result = UxSessionProjector.Project(CreateContext(
            playback: new ShellPlaybackStateSnapshot { Path = "C:\\Media\\current.mp4" },
            shellFlags: UxShellFlags.Default with
            {
                IsStartupGateBlocking = false
            }));

        Assert.Equal(UxSessionState.Watching, result.Primary);
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
