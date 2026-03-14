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
        var translatedText = snapshot.Translation.IsEnabled
            ? snapshot.SubtitlePresentation.TranslationText?.Trim()
            : null;
        var statusText = snapshot.SubtitlePresentation.StatusText?.Trim();

        return renderMode switch
        {
            SubtitleRenderMode.SourceOnly => BuildSourceOnlyPresentation(sourceText, statusText),
            SubtitleRenderMode.TranslationOnly => BuildTranslationOnlyPresentation(translatedText, statusText),
            SubtitleRenderMode.Dual => BuildDualPresentation(sourceText, translatedText, statusText),
            _ => new SubtitlePresentationModel()
        };
    }

    public SubtitleRenderMode GetEffectiveRenderMode(
        MediaSessionSnapshot snapshot,
        SubtitleRenderMode requestedMode,
        bool sourceOnlyOverrideForCurrentVideo = false)
    {
        if (requestedMode == SubtitleRenderMode.Off)
        {
            return SubtitleRenderMode.Off;
        }

        if (sourceOnlyOverrideForCurrentVideo)
        {
            return SubtitleRenderMode.SourceOnly;
        }

        if (!snapshot.Translation.IsEnabled && requestedMode == SubtitleRenderMode.Dual)
        {
            return SubtitleRenderMode.SourceOnly;
        }

        return requestedMode;
    }

    private static SubtitlePresentationModel BuildSourceOnlyPresentation(string? sourceText, string? statusText)
    {
        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            return new SubtitlePresentationModel
            {
                IsVisible = true,
                PrimaryText = sourceText
            };
        }

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            return new SubtitlePresentationModel
            {
                IsVisible = true,
                PrimaryText = statusText
            };
        }

        return new SubtitlePresentationModel();
    }

    private static SubtitlePresentationModel BuildTranslationOnlyPresentation(string? translatedText, string? statusText)
    {
        if (!string.IsNullOrWhiteSpace(translatedText))
        {
            return new SubtitlePresentationModel
            {
                IsVisible = true,
                PrimaryText = translatedText
            };
        }

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            return new SubtitlePresentationModel
            {
                IsVisible = true,
                PrimaryText = statusText
            };
        }

        return new SubtitlePresentationModel();
    }

    private static SubtitlePresentationModel BuildDualPresentation(string? sourceText, string? translatedText, string? statusText)
    {
        if (!string.IsNullOrWhiteSpace(translatedText) && !string.IsNullOrWhiteSpace(sourceText))
        {
            var normalizedSource = sourceText.Trim();
            var normalizedTranslation = translatedText.Trim();
            if (string.Equals(normalizedSource, normalizedTranslation, StringComparison.Ordinal))
            {
                return new SubtitlePresentationModel
                {
                    IsVisible = true,
                    PrimaryText = normalizedTranslation
                };
            }

            return new SubtitlePresentationModel
            {
                IsVisible = true,
                PrimaryText = normalizedTranslation,
                SecondaryText = normalizedSource
            };
        }

        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            return new SubtitlePresentationModel
            {
                IsVisible = true,
                PrimaryText = sourceText
            };
        }

        if (!string.IsNullOrWhiteSpace(translatedText))
        {
            return new SubtitlePresentationModel
            {
                IsVisible = true,
                PrimaryText = translatedText
            };
        }

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            return new SubtitlePresentationModel
            {
                IsVisible = true,
                PrimaryText = statusText
            };
        }

        return new SubtitlePresentationModel();
    }
}
