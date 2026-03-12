using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed record MediaSessionSnapshot
{
    public MediaSourceState Source { get; init; } = new();
    public MediaTimelineState Timeline { get; init; } = new();
    public MediaStreamState Streams { get; init; } = new();
    public TranscriptLane Transcript { get; init; } = new();
    public TranslationLane Translation { get; init; } = new();
    public SubtitlePresentationState SubtitlePresentation { get; init; } = new();
    public LanguageAnalysisState LanguageAnalysis { get; init; } = new();
    public AudioAugmentationLane AudioAugmentation { get; init; } = new();
}

public sealed record MediaSourceState
{
    public string? Path { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public bool IsLoaded { get; init; }
}

public sealed record MediaTimelineState
{
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public double Rate { get; init; } = 1.0;
    public bool IsPaused { get; init; } = true;
    public bool IsSeekable { get; init; }
    public DateTimeOffset SampledAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool HasVideo { get; init; }
    public bool HasAudio { get; init; }
    public int VideoWidth { get; init; }
    public int VideoHeight { get; init; }
    public int VideoDisplayWidth { get; init; }
    public int VideoDisplayHeight { get; init; }
    public bool IsMuted { get; init; }
    public double Volume { get; init; }
    public string ActiveHardwareDecoder { get; init; } = string.Empty;
}

public sealed record MediaStreamState
{
    public IReadOnlyList<MediaTrackInfo> Tracks { get; init; } = [];
    public int? ActiveAudioTrackId { get; init; }
    public int? ActiveSubtitleTrackId { get; init; }
}

public sealed record TranscriptLane
{
    public SubtitlePipelineSource Source { get; init; } = SubtitlePipelineSource.None;
    public bool IsGenerating { get; init; }
    public string? StatusText { get; init; }
    public IReadOnlyList<TranscriptSegment> Segments { get; init; } = [];
}

public sealed record TranslationLane
{
    public bool IsEnabled { get; init; }
    public bool AutoTranslateEnabled { get; init; }
    public string? StatusText { get; init; }
    public IReadOnlyList<TranslationSegment> Segments { get; init; } = [];
}

public sealed record SubtitlePresentationState
{
    public string? ActiveTranscriptSegmentId { get; init; }
    public string? ActiveTranslationSegmentId { get; init; }
    public string SourceText { get; init; } = string.Empty;
    public string TranslationText { get; init; } = string.Empty;
    public string? StatusText { get; init; }
}

public sealed record LanguageAnalysisState
{
    public string CurrentSourceLanguage { get; init; } = "und";
    public string TargetLanguage { get; init; } = "en";
    public IReadOnlyList<LanguageDetectionResult> Results { get; init; } = [];
}

public sealed record AudioAugmentationLane
{
    public IReadOnlyList<AugmentedAudioSegment> Segments { get; init; } = [];
}

public sealed record LanguageDetectionResult
{
    public string Language { get; init; } = "und";
    public double Confidence { get; init; }
    public string? Source { get; init; }
}

public sealed record AugmentedAudioSegment
{
    public string Id { get; init; } = string.Empty;
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string Language { get; init; } = "und";
    public SegmentProvenance Provenance { get; init; } = SegmentProvenance.Empty;
    public SegmentRevision Revision { get; init; } = SegmentRevision.Initial;
}

public readonly record struct TranscriptSegmentId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct TranslationSegmentId(string Value)
{
    public override string ToString() => Value;
}

public sealed record SegmentRevision(int Value)
{
    public static SegmentRevision Initial { get; } = new(1);

    public SegmentRevision Next() => new(Value + 1);
}

public sealed record SegmentProvenance
{
    public static SegmentProvenance Empty { get; } = new();

    public SubtitlePipelineSource Source { get; init; } = SubtitlePipelineSource.None;
    public string? Provider { get; init; }
    public string? ModelKey { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TranscriptSegment
{
    public TranscriptSegmentId Id { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Language { get; init; } = "und";
    public SegmentProvenance Provenance { get; init; } = SegmentProvenance.Empty;
    public SegmentRevision Revision { get; init; } = SegmentRevision.Initial;
}

public sealed record TranslationSegment
{
    public TranslationSegmentId Id { get; init; }
    public TranscriptSegmentId SourceSegmentId { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Language { get; init; } = "en";
    public SegmentProvenance Provenance { get; init; } = SegmentProvenance.Empty;
    public SegmentRevision Revision { get; init; } = SegmentRevision.Initial;
}

public sealed record SubtitlePresentationModel
{
    public bool IsVisible { get; init; }
    public string PrimaryText { get; init; } = string.Empty;
    public string SecondaryText { get; init; } = string.Empty;
}

public static class MediaSessionProjection
{
    public static PlaybackStateSnapshot ToPlaybackStateSnapshot(MediaSessionSnapshot snapshot)
    {
        return new PlaybackStateSnapshot
        {
            Path = snapshot.Source.Path,
            Position = snapshot.Timeline.Position,
            Duration = snapshot.Timeline.Duration,
            VideoWidth = snapshot.Timeline.VideoWidth,
            VideoHeight = snapshot.Timeline.VideoHeight,
            VideoDisplayWidth = snapshot.Timeline.VideoDisplayWidth,
            VideoDisplayHeight = snapshot.Timeline.VideoDisplayHeight,
            IsPaused = snapshot.Timeline.IsPaused,
            IsMuted = snapshot.Timeline.IsMuted,
            Volume = snapshot.Timeline.Volume,
            Speed = snapshot.Timeline.Rate,
            HasVideo = snapshot.Timeline.HasVideo,
            HasAudio = snapshot.Timeline.HasAudio,
            IsSeekable = snapshot.Timeline.IsSeekable,
            ActiveHardwareDecoder = snapshot.Timeline.ActiveHardwareDecoder
        };
    }

    public static SubtitleCue ToSubtitleCue(TranscriptSegment transcript, TranslationSegment? translation = null)
    {
        return new SubtitleCue
        {
            Start = transcript.Start,
            End = transcript.End,
            SourceText = transcript.Text,
            SourceLanguage = transcript.Language,
            TranslatedText = translation?.Text
        };
    }

    public static IReadOnlyList<SubtitleCue> ToSubtitleCues(MediaSessionSnapshot snapshot)
    {
        if (snapshot.Transcript.Segments.Count == 0)
        {
            return [];
        }

        var translations = snapshot.Translation.Segments
            .ToDictionary(segment => segment.SourceSegmentId.Value, StringComparer.Ordinal);

        return snapshot.Transcript.Segments
            .Select(segment =>
            {
                translations.TryGetValue(segment.Id.Value, out var translation);
                return ToSubtitleCue(segment, translation);
            })
            .ToArray();
    }

    public static SubtitleCue? ToActiveCue(MediaSessionSnapshot snapshot)
    {
        var activeTranscriptId = snapshot.SubtitlePresentation.ActiveTranscriptSegmentId;
        if (string.IsNullOrWhiteSpace(activeTranscriptId))
        {
            return null;
        }

        var transcript = snapshot.Transcript.Segments.FirstOrDefault(segment => string.Equals(segment.Id.Value, activeTranscriptId, StringComparison.Ordinal));
        if (transcript is null)
        {
            return null;
        }

        var translation = snapshot.Translation.Segments.FirstOrDefault(segment => string.Equals(segment.SourceSegmentId.Value, transcript.Id.Value, StringComparison.Ordinal));
        return ToSubtitleCue(transcript, translation);
    }
}

internal static class MediaSessionSnapshotCloner
{
    public static MediaSessionSnapshot Clone(MediaSessionSnapshot snapshot)
    {
        return snapshot with
        {
            Source = snapshot.Source with { },
            Timeline = snapshot.Timeline with { },
            Streams = snapshot.Streams with
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
                    .ToArray()
            },
            Transcript = snapshot.Transcript with
            {
                Segments = snapshot.Transcript.Segments
                    .Select(segment => segment with
                    {
                        Provenance = segment.Provenance with { },
                        Revision = segment.Revision with { }
                    })
                    .ToArray()
            },
            Translation = snapshot.Translation with
            {
                Segments = snapshot.Translation.Segments
                    .Select(segment => segment with
                    {
                        Provenance = segment.Provenance with { },
                        Revision = segment.Revision with { }
                    })
                    .ToArray()
            },
            SubtitlePresentation = snapshot.SubtitlePresentation with { },
            LanguageAnalysis = snapshot.LanguageAnalysis with
            {
                Results = snapshot.LanguageAnalysis.Results
                    .Select(result => result with { })
                    .ToArray()
            },
            AudioAugmentation = snapshot.AudioAugmentation with
            {
                Segments = snapshot.AudioAugmentation.Segments
                    .Select(segment => segment with
                    {
                        Provenance = segment.Provenance with { },
                        Revision = segment.Revision with { }
                    })
                    .ToArray()
            }
        };
    }
}

internal static class SegmentIdentity
{
    public static TranscriptSegmentId CreateTranscriptId(string? mediaPath, TimeSpan start, TimeSpan end, string text)
    {
        return new TranscriptSegmentId($"tr:{ComputeKey(mediaPath, start, end)}");
    }

    public static TranslationSegmentId CreateTranslationId(TranscriptSegmentId sourceSegmentId, string language, string text)
    {
        return new TranslationSegmentId($"tl:{ComputeKey(sourceSegmentId.Value, TimeSpan.Zero, TimeSpan.Zero, language)}");
    }

    private static string ComputeKey(string? mediaPath, TimeSpan start, TimeSpan end, string discriminator = "")
    {
        var input = $"{mediaPath}|{start.Ticks}|{end.Ticks}|{discriminator}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).Substring(0, 16);
    }
}
