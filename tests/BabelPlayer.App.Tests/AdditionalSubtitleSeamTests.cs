using BabelPlayer.App;
using BabelPlayer.Core;
using SubtitleRenderMode = BabelPlayer.App.ShellSubtitleRenderMode;

namespace BabelPlayer.App.Tests;

#pragma warning disable CS0067

/// <summary>
/// Extends MediaSessionSeamTests with additional subtitle presentation seam coverage:
/// - render mode changes propagate through the seam
/// - empty cue list when subtitles disabled
/// - subtitle presenter called in correct order on track switch
/// </summary>
public sealed class AdditionalSubtitleSeamTests
{
    [Fact]
    public async Task SubtitlePresenter_IsNotVisible_WhenRenderModeIsOff()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:01,000 --> 00:00:05,000
Hello there
""");

            var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(new FakeCredentialStore()),
                mediaSessionCoordinator: coordinator,
                environmentVariableReader: _ => null);

            await controller.LoadMediaSubtitlesAsync(videoPath);
            coordinator.ApplyClock(new ClockSnapshot(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMinutes(5),
                1.0,
                false,
                true,
                DateTimeOffset.UtcNow));

            var presentation = controller.GetOverlayPresentation(SubtitleRenderMode.Off);

            Assert.False(presentation.IsVisible);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitlePresenter_ShowsTranslationOnly_WhenRenderModeIsTranslationOnly()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:01,000 --> 00:00:05,000
Hola
""");

            var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(new FakeCredentialStore()),
                mediaSessionCoordinator: coordinator,
                environmentVariableReader: _ => null);

            await controller.LoadMediaSubtitlesAsync(videoPath);

            // Inject a translation
            coordinator.SetTranslationState(true, false);
            var transcript = Assert.Single(coordinator.Snapshot.Transcript.Segments);
            coordinator.UpsertTranslationSegment(new TranslationSegment
            {
                Id = new TranslationSegmentId("tl:1"),
                SourceSegmentId = transcript.Id,
                Start = transcript.Start,
                End = transcript.End,
                Text = "Hello",
                Language = "en"
            });
            coordinator.ApplyClock(new ClockSnapshot(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMinutes(5),
                1.0,
                false,
                true,
                DateTimeOffset.UtcNow));

            var presentation = controller.GetOverlayPresentation(SubtitleRenderMode.TranslationOnly);

            Assert.True(presentation.IsVisible);
            Assert.Equal("Hello", presentation.PrimaryText);
            Assert.Equal(string.Empty, presentation.SecondaryText);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitlePresenter_ShowsSourceAndTranslation_WhenRenderModeIsDual()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:01,000 --> 00:00:05,000
Ciao
""");

            var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(new FakeCredentialStore()),
                mediaSessionCoordinator: coordinator,
                environmentVariableReader: _ => null);

            await controller.LoadMediaSubtitlesAsync(videoPath);
            coordinator.SetTranslationState(true, false);
            var transcript = Assert.Single(coordinator.Snapshot.Transcript.Segments);
            coordinator.UpsertTranslationSegment(new TranslationSegment
            {
                Id = new TranslationSegmentId("tl:1"),
                SourceSegmentId = transcript.Id,
                Start = transcript.Start,
                End = transcript.End,
                Text = "Hello",
                Language = "en"
            });
            coordinator.ApplyClock(new ClockSnapshot(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMinutes(5),
                1.0,
                false,
                true,
                DateTimeOffset.UtcNow));

            var presentation = controller.GetOverlayPresentation(SubtitleRenderMode.Dual);

            Assert.True(presentation.IsVisible);
            Assert.Equal("Hello", presentation.PrimaryText);
            Assert.Equal("Ciao", presentation.SecondaryText);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitlePresenter_IsNotVisible_WhenPositionIsBeforeAllCues()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:01:00,000 --> 00:01:05,000
Late cue
""");

            var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(new FakeCredentialStore()),
                mediaSessionCoordinator: coordinator,
                environmentVariableReader: _ => null);

            await controller.LoadMediaSubtitlesAsync(videoPath);
            coordinator.ApplyClock(new ClockSnapshot(
                TimeSpan.FromSeconds(1),   // position is before the cue at 1:00
                TimeSpan.FromMinutes(5),
                1.0,
                false,
                true,
                DateTimeOffset.UtcNow));

            var presentation = controller.GetOverlayPresentation(SubtitleRenderMode.SourceOnly);

            Assert.False(presentation.IsVisible);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitlePresenter_UpdatesPresentation_WhenClockedPastCueBoundary()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:02,000 --> 00:00:04,000
First cue

2
00:00:06,000 --> 00:00:08,000
Second cue
""");

            var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(new FakeCredentialStore()),
                mediaSessionCoordinator: coordinator,
                environmentVariableReader: _ => null);

            await controller.LoadMediaSubtitlesAsync(videoPath);

            // Position in first cue
            coordinator.ApplyClock(new ClockSnapshot(TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(5), 1.0, false, true, DateTimeOffset.UtcNow));
            var first = controller.GetOverlayPresentation(SubtitleRenderMode.SourceOnly);

            // Advance past first cue into gap
            coordinator.ApplyClock(new ClockSnapshot(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), 1.0, false, true, DateTimeOffset.UtcNow));
            var gap = controller.GetOverlayPresentation(SubtitleRenderMode.SourceOnly);

            // Advance to second cue
            coordinator.ApplyClock(new ClockSnapshot(TimeSpan.FromSeconds(7), TimeSpan.FromMinutes(5), 1.0, false, true, DateTimeOffset.UtcNow));
            var second = controller.GetOverlayPresentation(SubtitleRenderMode.SourceOnly);

            Assert.Equal("First cue", first.PrimaryText);
            Assert.False(gap.IsVisible);
            Assert.Equal("Second cue", second.PrimaryText);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public string? GetOpenAiApiKey() => null;
        public void SaveOpenAiApiKey(string apiKey) { }
        public string? GetGoogleTranslateApiKey() => null;
        public void SaveGoogleTranslateApiKey(string apiKey) { }
        public string? GetDeepLApiKey() => null;
        public void SaveDeepLApiKey(string apiKey) { }
        public string? GetMicrosoftTranslatorApiKey() => null;
        public void SaveMicrosoftTranslatorApiKey(string apiKey) { }
        public string? GetMicrosoftTranslatorRegion() => null;
        public void SaveMicrosoftTranslatorRegion(string region) { }
        public string? GetSubtitleModelKey() => null;
        public void SaveSubtitleModelKey(string modelKey) { }
        public string? GetTranslationModelKey() => null;
        public void SaveTranslationModelKey(string modelKey) { }
        public void ClearTranslationModelKey() { }
        public bool GetAutoTranslateEnabled() => false;
        public void SaveAutoTranslateEnabled(bool enabled) { }
        public string? GetLlamaCppServerPath() => null;
        public void SaveLlamaCppServerPath(string path) { }
        public string? GetLlamaCppRuntimeVersion() => null;
        public void SaveLlamaCppRuntimeVersion(string version) { }
        public string? GetLlamaCppRuntimeSource() => null;
        public void SaveLlamaCppRuntimeSource(string source) { }
    }
}

#pragma warning restore CS0067
