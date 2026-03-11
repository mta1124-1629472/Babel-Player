using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed partial class SubtitleApplicationService
{
    private bool TryLoadCachedGeneratedSubtitles(string videoPath, string transcriptionModelKey)
    {
        if (!_generatedSubtitleCache.TryGetValue(GetGeneratedSubtitleCacheKey(videoPath, transcriptionModelKey), out var cachedCues))
        {
            return false;
        }

        var clonedCues = CloneCues(cachedCues);
        var currentSourceLanguage = ApplySourceLanguageToCues(clonedCues);
        UpdateWorkflowState(state => state with
        {
            CurrentVideoPath = videoPath,
            CurrentSourceLanguage = currentSourceLanguage,
            OverlayStatus = null
        });
        _mediaSessionCoordinator.SetTranscriptSegments(
            BuildTranscriptSegments(clonedCues, SubtitlePipelineSource.Generated, videoPath, transcriptionModelKey),
            SubtitlePipelineSource.Generated,
            currentSourceLanguage);
        _mediaSessionCoordinator.SetCaptionGenerationState(false);
        return true;
    }

    private void CacheGeneratedSubtitles(string videoPath, string transcriptionModelKey, IReadOnlyList<SubtitleCue> cues)
    {
        _generatedSubtitleCache[GetGeneratedSubtitleCacheKey(videoPath, transcriptionModelKey)] = CloneCues(cues).ToList();
    }

    private static string GetGeneratedSubtitleCacheKey(string videoPath, string transcriptionModelKey)
        => $"{Path.GetFullPath(videoPath)}|{transcriptionModelKey}";

    private static IReadOnlyList<SubtitleCue> CloneCues(IReadOnlyList<SubtitleCue> cues)
    {
        return cues.Select(cue => new SubtitleCue
        {
            Start = cue.Start,
            End = cue.End,
            SourceText = cue.SourceText,
            SourceLanguage = cue.SourceLanguage,
            TranslatedText = cue.TranslatedText
        }).ToArray();
    }

    private IReadOnlyList<TranscriptSegment> BuildTranscriptSegments(
        IReadOnlyList<SubtitleCue> cues,
        SubtitlePipelineSource source,
        string? mediaPath,
        string? modelKey = null)
    {
        return cues.Select(cue => BuildTranscriptSegment(cue, source, mediaPath, modelKey)).ToArray();
    }

    private TranscriptSegment BuildTranscriptSegment(
        SubtitleCue cue,
        SubtitlePipelineSource source,
        string? mediaPath,
        string? modelKey = null)
    {
        return new TranscriptSegment
        {
            Id = SegmentIdentity.CreateTranscriptId(mediaPath, cue.Start, cue.End, cue.SourceText),
            Start = cue.Start,
            End = cue.End,
            Text = cue.SourceText,
            Language = cue.SourceLanguage ?? DefaultSourceLanguage,
            Provenance = new SegmentProvenance
            {
                Source = source,
                Provider = source == SubtitlePipelineSource.Generated
                    ? SubtitleWorkflowCatalog.GetTranscriptionModel(_workflowStateStore.Snapshot.SelectedTranscriptionModelKey).DisplayName
                    : source.ToString(),
                ModelKey = modelKey
            },
            Revision = SegmentRevision.Initial
        };
    }

    private TranslationSegment CreateTranslationSegment(TranscriptSegment transcriptSegment, string translatedText)
    {
        return new TranslationSegment
        {
            Id = SegmentIdentity.CreateTranslationId(transcriptSegment.Id, _translationTargetLanguage, translatedText),
            SourceSegmentId = transcriptSegment.Id,
            Start = transcriptSegment.Start,
            End = transcriptSegment.End,
            Text = translatedText,
            Language = _translationTargetLanguage,
            Provenance = new SegmentProvenance
            {
                Source = _mediaSessionCoordinator.Snapshot.Transcript.Source,
                Provider = SubtitleWorkflowCatalog.GetTranslationModel(_workflowStateStore.Snapshot.SelectedTranslationModelKey).DisplayName,
                ModelKey = _workflowStateStore.Snapshot.SelectedTranslationModelKey
            },
            Revision = SegmentRevision.Initial
        };
    }

    private string ApplySourceLanguageToCues(IReadOnlyList<SubtitleCue> cues)
    {
        var detectedLanguage = ResolveSourceLanguage(string.Join(" ", cues.Take(6).Select(cue => cue.SourceText)));
        foreach (var cue in cues)
        {
            cue.SourceLanguage ??= detectedLanguage;
        }

        return detectedLanguage;
    }

    private static TranscriptSegment? GetActiveTranscriptSegment(MediaSessionSnapshot snapshot)
    {
        var activeId = snapshot.SubtitlePresentation.ActiveTranscriptSegmentId;
        return string.IsNullOrWhiteSpace(activeId)
            ? null
            : snapshot.Transcript.Segments.FirstOrDefault(segment => string.Equals(segment.Id.Value, activeId, StringComparison.Ordinal));
    }

    private static bool HasTranslatedSegment(TranscriptSegment transcriptSegment, MediaSessionSnapshot snapshot)
    {
        return snapshot.Translation.Segments.Any(segment => string.Equals(segment.SourceSegmentId.Value, transcriptSegment.Id.Value, StringComparison.Ordinal));
    }

    private static bool ShouldDisableCloudForError(Exception ex)
    {
        return ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("rate", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSourceLanguage(string text) => LanguageDetector.Detect(text);

    private static string ResolveAggregateSourceLanguage(string currentLanguage, string? nextLanguage)
    {
        if (string.IsNullOrWhiteSpace(nextLanguage) || string.Equals(nextLanguage, DefaultSourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return currentLanguage;
        }

        if (string.IsNullOrWhiteSpace(currentLanguage) || string.Equals(currentLanguage, DefaultSourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return nextLanguage;
        }

        if (IsEnglishLanguage(currentLanguage))
        {
            return nextLanguage;
        }

        return currentLanguage;
    }

    private static bool IsEnglishLanguage(string? languageCode)
    {
        return string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase)
            || string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLanguageCode(string? actualLanguageCode, string? expectedLanguageCode)
    {
        if (string.IsNullOrWhiteSpace(actualLanguageCode) || string.IsNullOrWhiteSpace(expectedLanguageCode))
        {
            return false;
        }

        if (string.Equals(expectedLanguageCode, "en", StringComparison.OrdinalIgnoreCase))
        {
            return IsEnglishLanguage(actualLanguageCode);
        }

        return string.Equals(actualLanguageCode, expectedLanguageCode, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatClock(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }

    private static string FormatSubtitleModelTransferStatus(ModelTransferProgress progress)
    {
        var modelName = progress.ModelLabel switch
        {
            "TinyEn" => "Local Tiny.en",
            "BaseEn" => "Local Base.en",
            "SmallEn" => "Local Small.en",
            _ => progress.ModelLabel
        };

        return progress.Stage switch
        {
            "downloading" => progress.ProgressRatio is double ratio
                ? $"Downloading {modelName} for subtitles... {ratio:P0}."
                : $"Downloading {modelName} for subtitles...",
            "loading" => $"Loading {modelName} for subtitles...",
            "ready" => $"{modelName} is ready. Generating captions...",
            _ => $"Preparing {modelName} for subtitles..."
        };
    }
}
