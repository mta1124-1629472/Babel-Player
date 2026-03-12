using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class SubtitlePresentationProjector
{
    public SubtitlePresentationModel Build(
        MediaSessionSnapshot snapshot,
        SubtitleRenderMode requestedMode,
        bool subtitlesVisible = true,
        bool sourceOnlyOverrideForCurrentVideo = false)
    {
        var renderMode = GetEffectiveRenderMode(snapshot, requestedMode, sourceOnlyOverrideForCurrentVideo);
        if (!subtitlesVisible || renderMode == SubtitleRenderMode.Off)
        {
            return new SubtitlePresentationModel();
        }

        var sourceText = snapshot.SubtitlePresentation.SourceText?.Trim();
        var translatedText = snapshot.SubtitlePresentation.TranslationText?.Trim();
        if (string.IsNullOrWhiteSpace(translatedText) && string.IsNullOrWhiteSpace(sourceText))
        {
            translatedText = snapshot.SubtitlePresentation.StatusText?.Trim();
        }

        if (string.IsNullOrWhiteSpace(sourceText) && string.IsNullOrWhiteSpace(translatedText))
        {
            return new SubtitlePresentationModel();
        }

        var showSecondaryLine = renderMode == SubtitleRenderMode.Dual
            && !string.IsNullOrWhiteSpace(sourceText)
            && !string.Equals(sourceText, translatedText, StringComparison.Ordinal);

        var primaryText = renderMode switch
        {
            SubtitleRenderMode.SourceOnly => sourceText,
            SubtitleRenderMode.TranslationOnly => translatedText,
            SubtitleRenderMode.Dual when !string.IsNullOrWhiteSpace(translatedText) => translatedText,
            SubtitleRenderMode.Dual => sourceText,
            _ => translatedText
        };

        if (string.IsNullOrWhiteSpace(primaryText))
        {
            primaryText = sourceText;
        }

        return new SubtitlePresentationModel
        {
            IsVisible = !string.IsNullOrWhiteSpace(primaryText) || showSecondaryLine,
            PrimaryText = primaryText ?? string.Empty,
            SecondaryText = showSecondaryLine ? sourceText ?? string.Empty : string.Empty
        };
    }

    public SubtitleRenderMode GetEffectiveRenderMode(
        MediaSessionSnapshot snapshot,
        SubtitleRenderMode requestedMode,
        bool sourceOnlyOverrideForCurrentVideo = false)
    {
        if (requestedMode == SubtitleRenderMode.Off || !snapshot.Translation.IsEnabled)
        {
            return requestedMode;
        }

        if (sourceOnlyOverrideForCurrentVideo)
        {
            return SubtitleRenderMode.SourceOnly;
        }

        if (requestedMode != SubtitleRenderMode.SourceOnly)
        {
            return requestedMode;
        }

        var sourceText = snapshot.SubtitlePresentation.SourceText?.Trim();
        var translatedText = snapshot.SubtitlePresentation.TranslationText?.Trim();
        return !string.IsNullOrWhiteSpace(translatedText)
               && !string.Equals(sourceText, translatedText, StringComparison.Ordinal)
            ? SubtitleRenderMode.TranslationOnly
            : SubtitleRenderMode.SourceOnly;
    }
}
