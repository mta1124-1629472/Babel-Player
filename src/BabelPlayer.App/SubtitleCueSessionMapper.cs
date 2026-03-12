using BabelPlayer.Core;

namespace BabelPlayer.App;

internal static class SubtitleCueSessionMapper
{
    public static IReadOnlyList<SubtitleCue> CloneCues(IReadOnlyList<SubtitleCue> cues)
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

    public static IReadOnlyList<TranscriptSegment> BuildTranscriptSegments(
        IReadOnlyList<SubtitleCue> cues,
        SubtitlePipelineSource source,
        string? mediaPath,
        string? modelKey,
        string generatedProviderDisplayName,
        string defaultSourceLanguage)
    {
        return cues.Select(cue => BuildTranscriptSegment(cue, source, mediaPath, modelKey, generatedProviderDisplayName, defaultSourceLanguage)).ToArray();
    }

    public static TranscriptSegment BuildTranscriptSegment(
        SubtitleCue cue,
        SubtitlePipelineSource source,
        string? mediaPath,
        string? modelKey,
        string generatedProviderDisplayName,
        string defaultSourceLanguage)
    {
        return new TranscriptSegment
        {
            Id = SegmentIdentity.CreateTranscriptId(mediaPath, cue.Start, cue.End, cue.SourceText),
            Start = cue.Start,
            End = cue.End,
            Text = cue.SourceText,
            Language = cue.SourceLanguage ?? defaultSourceLanguage,
            Provenance = new SegmentProvenance
            {
                Source = source,
                Provider = source == SubtitlePipelineSource.Generated
                    ? generatedProviderDisplayName
                    : source.ToString(),
                ModelKey = modelKey
            },
            Revision = SegmentRevision.Initial
        };
    }

    public static TranslationSegment CreateTranslationSegment(
        TranscriptSegment transcriptSegment,
        string translatedText,
        SubtitlePipelineSource source,
        string translationDisplayName,
        string? translationModelKey,
        string targetLanguage)
    {
        return new TranslationSegment
        {
            Id = SegmentIdentity.CreateTranslationId(transcriptSegment.Id, targetLanguage, translatedText),
            SourceSegmentId = transcriptSegment.Id,
            Start = transcriptSegment.Start,
            End = transcriptSegment.End,
            Text = translatedText,
            Language = targetLanguage,
            Provenance = new SegmentProvenance
            {
                Source = source,
                Provider = translationDisplayName,
                ModelKey = translationModelKey
            },
            Revision = SegmentRevision.Initial
        };
    }

    public static string ApplySourceLanguageToCues(IReadOnlyList<SubtitleCue> cues, string defaultSourceLanguage)
    {
        var detectedLanguage = ResolveSourceLanguage(string.Join(" ", cues.Take(6).Select(cue => cue.SourceText)), defaultSourceLanguage);
        foreach (var cue in cues)
        {
            cue.SourceLanguage ??= detectedLanguage;
        }

        return detectedLanguage;
    }

    public static TranscriptSegment? GetActiveTranscriptSegment(MediaSessionSnapshot snapshot)
    {
        var activeId = snapshot.SubtitlePresentation.ActiveTranscriptSegmentId;
        return string.IsNullOrWhiteSpace(activeId)
            ? null
            : snapshot.Transcript.Segments.FirstOrDefault(segment => string.Equals(segment.Id.Value, activeId, StringComparison.Ordinal));
    }

    public static bool HasTranslatedSegment(TranscriptSegment transcriptSegment, MediaSessionSnapshot snapshot)
    {
        return snapshot.Translation.Segments.Any(segment => string.Equals(segment.SourceSegmentId.Value, transcriptSegment.Id.Value, StringComparison.Ordinal));
    }

    public static bool ShouldDisableCloudForError(Exception ex)
    {
        return ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("rate", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveAggregateSourceLanguage(string currentLanguage, string? nextLanguage, string defaultSourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(nextLanguage) || string.Equals(nextLanguage, defaultSourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return currentLanguage;
        }

        if (string.IsNullOrWhiteSpace(currentLanguage) || string.Equals(currentLanguage, defaultSourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return nextLanguage;
        }

        if (IsEnglishLanguage(currentLanguage))
        {
            return nextLanguage;
        }

        return currentLanguage;
    }

    public static bool IsLanguageCode(string? actualLanguageCode, string? expectedLanguageCode)
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

    public static string FormatClock(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }

    public static string FormatSubtitleModelTransferStatus(ModelTransferProgress progress)
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

    public static string ResolveSourceLanguage(string text, string defaultSourceLanguage)
    {
        var detected = LanguageDetector.Detect(text);
        return string.IsNullOrWhiteSpace(detected) ? defaultSourceLanguage : detected;
    }

    private static bool IsEnglishLanguage(string? languageCode)
    {
        return string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase)
            || string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase);
    }
}