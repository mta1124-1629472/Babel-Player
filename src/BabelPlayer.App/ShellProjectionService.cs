using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed record ShellProjectionSnapshot
{
    public ShellTransportProjection Transport { get; init; } = new();
    public ShellSelectedTracksProjection SelectedTracks { get; init; } = new();
    public ShellSubtitleProjection Subtitle { get; init; } = new();
}

public sealed record ShellTransportProjection
{
    public string? Path { get; init; }
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public string CurrentTimeText { get; init; } = "00:00";
    public string DurationText { get; init; } = "00:00";
    public bool IsPaused { get; init; } = true;
    public bool IsMuted { get; init; }
    public double Volume { get; init; } = 0.8;
    public double PlaybackRate { get; init; } = 1.0;
    public string ActiveHardwareDecoder { get; init; } = "mpv ready";
}

public sealed record ShellSelectedTracksProjection
{
    public IReadOnlyList<MediaTrackInfo> Tracks { get; init; } = [];
    public int? ActiveAudioTrackId { get; init; }
    public int? ActiveSubtitleTrackId { get; init; }
}

public sealed record ShellSubtitleProjection
{
    public string SourceText { get; init; } = string.Empty;
    public string TranslationText { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public string? ActiveTranscriptSegmentId { get; init; }
    public string? ActiveTranslationSegmentId { get; init; }
    public SubtitlePipelineSource Source { get; init; } = SubtitlePipelineSource.None;
    public bool IsCaptionGenerationInProgress { get; init; }
    public bool IsTranslationEnabled { get; init; }
    public bool IsAutoTranslateEnabled { get; init; }
}

public sealed class ShellProjectionService : IDisposable
{
    private readonly IMediaSessionStore _store;

    public ShellProjectionService(IMediaSessionStore store)
    {
        _store = store;
        Current = BuildProjection(_store.Snapshot);
        _store.SnapshotChanged += HandleSnapshotChanged;
    }

    public event Action<ShellProjectionSnapshot>? ProjectionChanged;

    public ShellProjectionSnapshot Current { get; private set; }

    public void Dispose()
    {
        _store.SnapshotChanged -= HandleSnapshotChanged;
    }

    private void HandleSnapshotChanged(MediaSessionSnapshot snapshot)
    {
        Current = BuildProjection(snapshot);
        ProjectionChanged?.Invoke(Current);
    }

    private static ShellProjectionSnapshot BuildProjection(MediaSessionSnapshot snapshot)
    {
        return new ShellProjectionSnapshot
        {
            Transport = BuildTransportProjection(snapshot),
            SelectedTracks = BuildTrackProjection(snapshot),
            Subtitle = BuildSubtitleProjection(snapshot)
        };
    }

    private static ShellTransportProjection BuildTransportProjection(MediaSessionSnapshot snapshot)
    {
        return new ShellTransportProjection
        {
            Path = snapshot.Source.Path,
            PositionSeconds = snapshot.Timeline.Position.TotalSeconds,
            DurationSeconds = snapshot.Timeline.Duration.TotalSeconds,
            CurrentTimeText = FormatPlaybackClock(snapshot.Timeline.Position),
            DurationText = snapshot.Timeline.Duration > TimeSpan.Zero ? FormatPlaybackClock(snapshot.Timeline.Duration) : "00:00",
            IsPaused = snapshot.Timeline.IsPaused,
            IsMuted = snapshot.Timeline.IsMuted,
            Volume = snapshot.Timeline.Volume,
            PlaybackRate = snapshot.Timeline.Rate,
            ActiveHardwareDecoder = string.IsNullOrWhiteSpace(snapshot.Timeline.ActiveHardwareDecoder)
                ? "mpv ready"
                : snapshot.Timeline.ActiveHardwareDecoder
        };
    }

    private static ShellSelectedTracksProjection BuildTrackProjection(MediaSessionSnapshot snapshot)
    {
        return new ShellSelectedTracksProjection
        {
            Tracks = snapshot.Streams.Tracks
                .Select(track => new MediaTrackInfo
                {
                    Id = track.Id,
                    FfIndex = track.FfIndex,
                    Kind = track.Kind,
                    Title = track.Title,
                    Language = track.Language,
                    Codec = track.Codec,
                    IsEmbedded = track.IsEmbedded,
                    IsSelected = track.IsSelected,
                    IsTextBased = track.IsTextBased
                })
                .ToArray(),
            ActiveAudioTrackId = snapshot.Streams.ActiveAudioTrackId,
            ActiveSubtitleTrackId = snapshot.Streams.ActiveSubtitleTrackId
        };
    }

    private static ShellSubtitleProjection BuildSubtitleProjection(MediaSessionSnapshot snapshot)
    {
        return new ShellSubtitleProjection
        {
            SourceText = snapshot.SubtitlePresentation.SourceText,
            TranslationText = snapshot.SubtitlePresentation.TranslationText,
            StatusText = snapshot.SubtitlePresentation.StatusText ?? string.Empty,
            ActiveTranscriptSegmentId = snapshot.SubtitlePresentation.ActiveTranscriptSegmentId,
            ActiveTranslationSegmentId = snapshot.SubtitlePresentation.ActiveTranslationSegmentId,
            Source = snapshot.Transcript.Source,
            IsCaptionGenerationInProgress = snapshot.Transcript.IsGenerating,
            IsTranslationEnabled = snapshot.Translation.IsEnabled,
            IsAutoTranslateEnabled = snapshot.Translation.AutoTranslateEnabled
        };
    }

    private static string FormatPlaybackClock(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }
}
