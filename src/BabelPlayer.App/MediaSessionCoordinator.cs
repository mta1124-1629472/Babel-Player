using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class MediaSessionCoordinator
{
    private readonly InMemoryMediaSessionStore _store;

    public MediaSessionCoordinator(InMemoryMediaSessionStore store)
    {
        _store = store;
    }

    public IMediaSessionStore Store => _store;

    public MediaSessionSnapshot Snapshot => _store.Snapshot;

    public void Reset(string? mediaPath = null, string? displayName = null)
    {
        _store.Update(_ => new MediaSessionSnapshot
        {
            Source = new MediaSourceState
            {
                Path = mediaPath,
                DisplayName = displayName ?? GetDisplayName(mediaPath),
                IsLoaded = !string.IsNullOrWhiteSpace(mediaPath)
            }
        });
    }

    public void OpenMedia(string mediaPath, string? displayName = null)
    {
        _store.Update(_ => new MediaSessionSnapshot
        {
            Source = new MediaSourceState
            {
                Path = mediaPath,
                DisplayName = displayName ?? GetDisplayName(mediaPath),
                IsLoaded = true
            }
        });
    }

    public void ApplyPlaybackState(PlaybackBackendState state)
    {
        _store.Update(snapshot =>
        {
            var updated = snapshot with
            {
                Source = snapshot.Source with
                {
                    Path = state.Path ?? snapshot.Source.Path,
                    DisplayName = string.IsNullOrWhiteSpace(snapshot.Source.DisplayName)
                        ? GetDisplayName(state.Path)
                        : snapshot.Source.DisplayName,
                    IsLoaded = !string.IsNullOrWhiteSpace(state.Path) || snapshot.Source.IsLoaded
                },
                Timeline = snapshot.Timeline with
                {
                    HasVideo = state.HasVideo,
                    HasAudio = state.HasAudio,
                    VideoWidth = state.VideoWidth,
                    VideoHeight = state.VideoHeight,
                    VideoDisplayWidth = state.VideoDisplayWidth,
                    VideoDisplayHeight = state.VideoDisplayHeight,
                    IsMuted = state.IsMuted,
                    Volume = state.Volume,
                    ActiveHardwareDecoder = state.ActiveHardwareDecoder
                }
            };

            return UpdatePresentation(updated);
        });
    }

    public void ApplyClock(ClockSnapshot clock)
    {
        _store.Update(snapshot => UpdatePresentation(snapshot with
        {
            Timeline = snapshot.Timeline with
            {
                Position = clock.Position,
                Duration = clock.Duration,
                Rate = clock.Rate,
                IsPaused = clock.IsPaused,
                IsSeekable = clock.IsSeekable,
                SampledAtUtc = clock.SampledAtUtc
            }
        }));
    }

    public void ApplyTracks(IReadOnlyList<MediaTrackInfo> tracks)
    {
        var cloned = tracks.Select(CloneTrack).ToArray();
        _store.Update(snapshot => snapshot with
        {
            Streams = new MediaStreamState
            {
                Tracks = cloned,
                ActiveAudioTrackId = cloned.FirstOrDefault(track => track.Kind == MediaTrackKind.Audio && track.IsSelected)?.Id,
                ActiveSubtitleTrackId = cloned.FirstOrDefault(track => track.Kind == MediaTrackKind.Subtitle && track.IsSelected)?.Id
            }
        });
    }

    public void SetTranscriptSegments(
        IReadOnlyList<TranscriptSegment> segments,
        SubtitlePipelineSource source,
        string currentSourceLanguage,
        bool isGenerating = false,
        string? statusText = null)
    {
        var cloned = segments
            .Select(segment => segment with { })
            .OrderBy(segment => segment.Start)
            .ToArray();

        _store.Update(snapshot => UpdatePresentation(snapshot with
        {
            Transcript = snapshot.Transcript with
            {
                Source = source,
                IsGenerating = isGenerating,
                StatusText = statusText,
                Segments = cloned
            },
            LanguageAnalysis = snapshot.LanguageAnalysis with
            {
                CurrentSourceLanguage = string.IsNullOrWhiteSpace(currentSourceLanguage) ? snapshot.LanguageAnalysis.CurrentSourceLanguage : currentSourceLanguage
            }
        }));
    }

    public void UpsertTranscriptSegment(TranscriptSegment segment, string? currentSourceLanguage = null, string? statusText = null)
    {
        _store.Update(snapshot =>
        {
            var updatedSegments = UpsertSegment(snapshot.Transcript.Segments, segment);
            return UpdatePresentation(snapshot with
            {
                Transcript = snapshot.Transcript with
                {
                    Segments = updatedSegments,
                    StatusText = statusText ?? snapshot.Transcript.StatusText
                },
                LanguageAnalysis = snapshot.LanguageAnalysis with
                {
                    CurrentSourceLanguage = string.IsNullOrWhiteSpace(currentSourceLanguage) ? snapshot.LanguageAnalysis.CurrentSourceLanguage : currentSourceLanguage
                }
            });
        });
    }

    public void ClearTranscriptSegments(
        SubtitlePipelineSource source = SubtitlePipelineSource.None,
        bool isGenerating = false,
        string? statusText = null,
        string currentSourceLanguage = "und")
    {
        _store.Update(snapshot => UpdatePresentation(snapshot with
        {
            Transcript = new TranscriptLane
            {
                Source = source,
                IsGenerating = isGenerating,
                StatusText = statusText
            },
            Translation = snapshot.Translation with
            {
                Segments = []
            },
            LanguageAnalysis = snapshot.LanguageAnalysis with
            {
                CurrentSourceLanguage = currentSourceLanguage
            }
        }));
    }

    public void SetCaptionGenerationState(bool isGenerating, string? statusText = null)
    {
        _store.Update(snapshot => snapshot with
        {
            Transcript = snapshot.Transcript with
            {
                IsGenerating = isGenerating,
                StatusText = statusText ?? snapshot.Transcript.StatusText
            }
        });
    }

    public void SetTranslationState(bool enabled, bool autoTranslateEnabled, string? statusText = null)
    {
        _store.Update(snapshot => UpdatePresentation(snapshot with
        {
            Translation = snapshot.Translation with
            {
                IsEnabled = enabled,
                AutoTranslateEnabled = autoTranslateEnabled,
                StatusText = statusText ?? snapshot.Translation.StatusText
            }
        }));
    }

    public void ReplaceTranslationSegments(IReadOnlyList<TranslationSegment> segments, string? statusText = null)
    {
        var cloned = segments
            .Select(segment => segment with { })
            .OrderBy(segment => segment.Start)
            .ToArray();

        _store.Update(snapshot => UpdatePresentation(snapshot with
        {
            Translation = snapshot.Translation with
            {
                Segments = cloned,
                StatusText = statusText ?? snapshot.Translation.StatusText
            }
        }));
    }

    public void UpsertTranslationSegment(TranslationSegment segment, string? statusText = null)
    {
        _store.Update(snapshot => UpdatePresentation(snapshot with
        {
            Translation = snapshot.Translation with
            {
                Segments = UpsertTranslationSegments(snapshot.Translation.Segments, segment),
                StatusText = statusText ?? snapshot.Translation.StatusText
            }
        }));
    }

    public void ClearTranslations(string? statusText = null)
    {
        _store.Update(snapshot => UpdatePresentation(snapshot with
        {
            Translation = snapshot.Translation with
            {
                Segments = [],
                StatusText = statusText ?? snapshot.Translation.StatusText
            }
        }));
    }

    public void SetSubtitleStatus(string? statusText)
    {
        _store.Update(snapshot => snapshot with
        {
            SubtitlePresentation = snapshot.SubtitlePresentation with
            {
                StatusText = statusText
            }
        });
    }

    public void SetLanguageAnalysis(string currentSourceLanguage, string? targetLanguage = null, IReadOnlyList<LanguageDetectionResult>? results = null)
    {
        _store.Update(snapshot => snapshot with
        {
            LanguageAnalysis = snapshot.LanguageAnalysis with
            {
                CurrentSourceLanguage = currentSourceLanguage,
                TargetLanguage = targetLanguage ?? snapshot.LanguageAnalysis.TargetLanguage,
                Results = results?.Select(result => result with { }).ToArray() ?? snapshot.LanguageAnalysis.Results
            }
        });
    }

    private static MediaSessionSnapshot UpdatePresentation(MediaSessionSnapshot snapshot)
    {
        var activeTranscript = GetActiveTranscript(snapshot.Transcript.Segments, snapshot.Timeline.Position);
        var activeTranslation = activeTranscript is null
            ? null
            : snapshot.Translation.Segments.FirstOrDefault(segment => string.Equals(segment.SourceSegmentId.Value, activeTranscript.Id.Value, StringComparison.Ordinal));

        return snapshot with
        {
            SubtitlePresentation = snapshot.SubtitlePresentation with
            {
                ActiveTranscriptSegmentId = activeTranscript?.Id.Value,
                ActiveTranslationSegmentId = activeTranslation?.Id.Value,
                SourceText = activeTranscript?.Text ?? string.Empty,
                TranslationText = activeTranslation?.Text ?? string.Empty
            }
        };
    }

    private static TranscriptSegment? GetActiveTranscript(IReadOnlyList<TranscriptSegment> segments, TimeSpan position)
    {
        TranscriptSegment? active = null;
        foreach (var segment in segments)
        {
            if (position < segment.Start || position > segment.End)
            {
                continue;
            }

            if (active is null || segment.Start > active.Start || (segment.Start == active.Start && segment.End > active.End))
            {
                active = segment;
            }
        }

        return active;
    }

    private static T[] UpsertSegment<T>(IReadOnlyList<T> segments, T segment)
        where T : class
    {
        var list = segments.ToList();
        var idProperty = typeof(T).GetProperty("Id") ?? throw new InvalidOperationException($"Type {typeof(T).Name} must expose Id.");
        var startProperty = typeof(T).GetProperty("Start");
        var endProperty = typeof(T).GetProperty("End");
        var segmentId = idProperty.GetValue(segment)?.ToString();
        var index = list.FindIndex(item => string.Equals(idProperty.GetValue(item)?.ToString(), segmentId, StringComparison.Ordinal));
        if (index >= 0)
        {
            list[index] = segment;
        }
        else
        {
            list.Add(segment);
        }

        if (startProperty is not null && endProperty is not null)
        {
            list = list
                .OrderBy(item => (TimeSpan)(startProperty.GetValue(item) ?? TimeSpan.Zero))
                .ThenBy(item => (TimeSpan)(endProperty.GetValue(item) ?? TimeSpan.Zero))
                .ToList();
        }

        return list.ToArray();
    }

    private static TranslationSegment[] UpsertTranslationSegments(IReadOnlyList<TranslationSegment> segments, TranslationSegment segment)
    {
        var list = segments
            .Where(existing => !string.Equals(existing.SourceSegmentId.Value, segment.SourceSegmentId.Value, StringComparison.Ordinal))
            .ToList();
        list.Add(segment);
        return list
            .OrderBy(item => item.Start)
            .ThenBy(item => item.End)
            .ToArray();
    }

    private static MediaTrackInfo CloneTrack(MediaTrackInfo track)
    {
        return new MediaTrackInfo
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
        };
    }

    private static string GetDisplayName(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
    }
}
