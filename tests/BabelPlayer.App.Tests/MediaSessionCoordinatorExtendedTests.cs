using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

public sealed class MediaSessionCoordinatorExtendedTests
{
    // ── Reset / OpenMedia ─────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsState_WhenNoPathGiven()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        coordinator.OpenMedia("C:\\Media\\video.mp4");

        coordinator.Reset();

        var snapshot = coordinator.Snapshot;
        Assert.Null(snapshot.Source.Path);
        Assert.False(snapshot.Source.IsLoaded);
        Assert.Empty(snapshot.Source.DisplayName);
    }

    [Fact]
    public void Reset_SetsPath_WhenPathProvided()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());

        coordinator.Reset("C:\\Media\\video.mp4");

        var snapshot = coordinator.Snapshot;
        Assert.Equal("C:\\Media\\video.mp4", snapshot.Source.Path);
        Assert.True(snapshot.Source.IsLoaded);
        Assert.Equal("video.mp4", snapshot.Source.DisplayName);
    }

    [Fact]
    public void OpenMedia_SetsLoadedStateWithDisplayName()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());

        coordinator.OpenMedia("C:\\Media\\movie.mkv");

        var snapshot = coordinator.Snapshot;
        Assert.Equal("C:\\Media\\movie.mkv", snapshot.Source.Path);
        Assert.True(snapshot.Source.IsLoaded);
        Assert.Equal("movie.mkv", snapshot.Source.DisplayName);
    }

    [Fact]
    public void OpenMedia_UsesCustomDisplayName_WhenProvided()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());

        coordinator.OpenMedia("C:\\Media\\movie.mkv", "My Movie");

        Assert.Equal("My Movie", coordinator.Snapshot.Source.DisplayName);
    }

    // ── ApplyPlaybackState ────────────────────────────────────────────────────

    [Fact]
    public void ApplyPlaybackState_UpdatesVideoDimensionsAndFlags()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());

        coordinator.ApplyPlaybackState(new PlaybackBackendState
        {
            Path = "C:\\video.mp4",
            HasVideo = true,
            HasAudio = true,
            VideoWidth = 1920,
            VideoHeight = 1080,
            VideoDisplayWidth = 1280,
            VideoDisplayHeight = 720,
            IsMuted = true,
            Volume = 0.5,
            ActiveHardwareDecoder = "d3d11va"
        });

        var tl = coordinator.Snapshot.Timeline;
        Assert.True(tl.HasVideo);
        Assert.True(tl.HasAudio);
        Assert.Equal(1920, tl.VideoWidth);
        Assert.Equal(1080, tl.VideoHeight);
        Assert.Equal(1280, tl.VideoDisplayWidth);
        Assert.Equal(720, tl.VideoDisplayHeight);
        Assert.True(tl.IsMuted);
        Assert.Equal(0.5, tl.Volume);
        Assert.Equal("d3d11va", tl.ActiveHardwareDecoder);
    }

    // ── ApplyClock ────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyClock_UpdatesTimelineAndActivatesMatchingSegment()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var segment = new TranscriptSegment
        {
            Id = new TranscriptSegmentId("tr:1"),
            Start = TimeSpan.FromSeconds(2),
            End = TimeSpan.FromSeconds(5),
            Text = "Active cue",
            Language = "en"
        };
        coordinator.SetTranscriptSegments([segment], SubtitlePipelineSource.Sidecar, "en");

        coordinator.ApplyClock(new ClockSnapshot(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMinutes(10),
            1.5,
            true,
            true,
            DateTimeOffset.UtcNow));

        var snapshot = coordinator.Snapshot;
        Assert.Equal(TimeSpan.FromSeconds(3), snapshot.Timeline.Position);
        Assert.Equal(TimeSpan.FromMinutes(10), snapshot.Timeline.Duration);
        Assert.Equal(1.5, snapshot.Timeline.Rate);
        Assert.True(snapshot.Timeline.IsPaused);
        Assert.True(snapshot.Timeline.IsSeekable);
        Assert.Equal("Active cue", snapshot.SubtitlePresentation.SourceText);
    }

    [Fact]
    public void ApplyClock_ClearsActiveSegment_WhenPositionBeforeAllSegments()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        coordinator.SetTranscriptSegments(
        [
            new TranscriptSegment
            {
                Id = new TranscriptSegmentId("tr:1"),
                Start = TimeSpan.FromSeconds(10),
                End = TimeSpan.FromSeconds(15),
                Text = "Later cue",
                Language = "en"
            }
        ], SubtitlePipelineSource.Sidecar, "en");

        coordinator.ApplyClock(new ClockSnapshot(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMinutes(5),
            1.0,
            false,
            true,
            DateTimeOffset.UtcNow));

        Assert.Equal(string.Empty, coordinator.Snapshot.SubtitlePresentation.SourceText);
        Assert.Null(coordinator.Snapshot.SubtitlePresentation.ActiveTranscriptSegmentId);
    }

    // ── ApplyTracks ───────────────────────────────────────────────────────────

    [Fact]
    public void ApplyTracks_SetsActiveAudioAndSubtitleTrackIds()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());

        coordinator.ApplyTracks(
        [
            new MediaTrackInfo { Id = 1, Kind = MediaTrackKind.Audio, IsSelected = true },
            new MediaTrackInfo { Id = 2, Kind = MediaTrackKind.Audio, IsSelected = false },
            new MediaTrackInfo { Id = 3, Kind = MediaTrackKind.Subtitle, IsSelected = true }
        ]);

        var streams = coordinator.Snapshot.Streams;
        Assert.Equal(1, streams.ActiveAudioTrackId);
        Assert.Equal(3, streams.ActiveSubtitleTrackId);
        Assert.Equal(3, streams.Tracks.Count);
    }

    [Fact]
    public void ApplyTracks_SetsNullActiveIds_WhenNoTracksAreSelected()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());

        coordinator.ApplyTracks(
        [
            new MediaTrackInfo { Id = 1, Kind = MediaTrackKind.Audio, IsSelected = false }
        ]);

        Assert.Null(coordinator.Snapshot.Streams.ActiveAudioTrackId);
        Assert.Null(coordinator.Snapshot.Streams.ActiveSubtitleTrackId);
    }

    // ── SetTranslationState ───────────────────────────────────────────────────

    [Fact]
    public void SetTranslationState_ReflectedInSnapshot()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());

        coordinator.SetTranslationState(enabled: true, autoTranslateEnabled: true, statusText: "Translating...");

        var tl = coordinator.Snapshot.Translation;
        Assert.True(tl.IsEnabled);
        Assert.True(tl.AutoTranslateEnabled);
        Assert.Equal("Translating...", tl.StatusText);
    }

    // ── ReplaceTranslationSegments ────────────────────────────────────────────

    [Fact]
    public void ReplaceTranslationSegments_OrdersSegmentsByStart()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        coordinator.SetTranscriptSegments(
        [
            new TranscriptSegment { Id = new TranscriptSegmentId("tr:1"), Start = TimeSpan.FromSeconds(0), End = TimeSpan.FromSeconds(2), Text = "A", Language = "en" }
        ], SubtitlePipelineSource.Sidecar, "en");

        coordinator.ReplaceTranslationSegments(
        [
            new TranslationSegment { Id = new TranslationSegmentId("tl:2"), SourceSegmentId = new TranscriptSegmentId("tr:2"), Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(12), Text = "Later", Language = "en" },
            new TranslationSegment { Id = new TranslationSegmentId("tl:1"), SourceSegmentId = new TranscriptSegmentId("tr:1"), Start = TimeSpan.FromSeconds(0), End = TimeSpan.FromSeconds(2), Text = "Earlier", Language = "en" }
        ]);

        var segments = coordinator.Snapshot.Translation.Segments;
        Assert.Equal(2, segments.Count);
        Assert.Equal(TimeSpan.FromSeconds(0), segments[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(10), segments[1].Start);
    }

    // ── UpsertTranslationSegment ──────────────────────────────────────────────

    [Fact]
    public void UpsertTranslationSegment_ReplacesBySourceSegmentId()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var sourceId = new TranscriptSegmentId("tr:1");

        coordinator.UpsertTranslationSegment(new TranslationSegment
        {
            Id = new TranslationSegmentId("tl:1"),
            SourceSegmentId = sourceId,
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "Old translation",
            Language = "en"
        });

        coordinator.UpsertTranslationSegment(new TranslationSegment
        {
            Id = new TranslationSegmentId("tl:1b"),
            SourceSegmentId = sourceId,
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "New translation",
            Language = "en"
        });

        var segments = coordinator.Snapshot.Translation.Segments;
        Assert.Single(segments);
        Assert.Equal("New translation", segments[0].Text);
    }

    // ── ClearTranslations ─────────────────────────────────────────────────────

    [Fact]
    public void ClearTranslations_RemovesAllTranslationSegments()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());

        coordinator.UpsertTranslationSegment(new TranslationSegment
        {
            Id = new TranslationSegmentId("tl:1"),
            SourceSegmentId = new TranscriptSegmentId("tr:1"),
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "Hello",
            Language = "en"
        });

        coordinator.ClearTranslations();

        Assert.Empty(coordinator.Snapshot.Translation.Segments);
    }

    // ── SetCaptionGenerationState ─────────────────────────────────────────────

    [Fact]
    public void SetCaptionGenerationState_UpdatesIsGeneratingAndStatus()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());

        coordinator.SetCaptionGenerationState(isGenerating: true, statusText: "Generating...");

        var transcript = coordinator.Snapshot.Transcript;
        Assert.True(transcript.IsGenerating);
        Assert.Equal("Generating...", transcript.StatusText);
    }

    [Fact]
    public void SetCaptionGenerationState_StopsGenerating()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        coordinator.SetCaptionGenerationState(isGenerating: true);

        coordinator.SetCaptionGenerationState(isGenerating: false);

        Assert.False(coordinator.Snapshot.Transcript.IsGenerating);
    }

    // ── SetLanguageAnalysis ───────────────────────────────────────────────────

    [Fact]
    public void SetLanguageAnalysis_UpdatesLanguageFields()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());

        coordinator.SetLanguageAnalysis("ja", "en");

        var lang = coordinator.Snapshot.LanguageAnalysis;
        Assert.Equal("ja", lang.CurrentSourceLanguage);
        Assert.Equal("en", lang.TargetLanguage);
    }

    // ── SetSubtitleStatus ─────────────────────────────────────────────────────

    [Fact]
    public void SetSubtitleStatus_UpdatesStatusText()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());

        coordinator.SetSubtitleStatus("Loading subtitles...");

        Assert.Equal("Loading subtitles...", coordinator.Snapshot.SubtitlePresentation.StatusText);
    }

    // ── ClearTranscriptSegments ───────────────────────────────────────────────

    [Fact]
    public void ClearTranscriptSegments_RemovesAllSegmentsAndTranslations()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var transcriptId = new TranscriptSegmentId("tr:1");
        coordinator.SetTranscriptSegments(
        [
            new TranscriptSegment { Id = transcriptId, Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), Text = "Hello", Language = "en" }
        ], SubtitlePipelineSource.Sidecar, "en");
        coordinator.UpsertTranslationSegment(new TranslationSegment
        {
            Id = new TranslationSegmentId("tl:1"),
            SourceSegmentId = transcriptId,
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "Hola",
            Language = "es"
        });

        coordinator.ClearTranscriptSegments(SubtitlePipelineSource.None, statusText: "Cleared");

        Assert.Empty(coordinator.Snapshot.Transcript.Segments);
        Assert.Empty(coordinator.Snapshot.Translation.Segments);
        Assert.Equal("Cleared", coordinator.Snapshot.Transcript.StatusText);
    }

    // ── Snapshot immutability ─────────────────────────────────────────────────

    [Fact]
    public void Snapshot_IsImmutable_AfterSubsequentUpdate()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        coordinator.OpenMedia("C:\\video.mp4");

        var firstSnapshot = coordinator.Snapshot;
        coordinator.OpenMedia("C:\\other.mp4");

        Assert.Equal("C:\\video.mp4", firstSnapshot.Source.Path);
        Assert.Equal("C:\\other.mp4", coordinator.Snapshot.Source.Path);
    }
}
