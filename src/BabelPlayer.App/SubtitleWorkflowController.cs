using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class SubtitleWorkflowController
{
    public SubtitleRenderMode ToggleSource(SubtitleRenderMode current)
    {
        return current switch
        {
            SubtitleRenderMode.Off => SubtitleRenderMode.SourceOnly,
            SubtitleRenderMode.SourceOnly => SubtitleRenderMode.Off,
            SubtitleRenderMode.TranslationOnly => SubtitleRenderMode.Dual,
            SubtitleRenderMode.Dual => SubtitleRenderMode.TranslationOnly,
            _ => SubtitleRenderMode.TranslationOnly
        };
    }

    public SubtitleRenderMode ToggleTranslation(SubtitleRenderMode current)
    {
        return current switch
        {
            SubtitleRenderMode.Off => SubtitleRenderMode.TranslationOnly,
            SubtitleRenderMode.SourceOnly => SubtitleRenderMode.Dual,
            SubtitleRenderMode.TranslationOnly => SubtitleRenderMode.Off,
            SubtitleRenderMode.Dual => SubtitleRenderMode.SourceOnly,
            _ => SubtitleRenderMode.TranslationOnly
        };
    }

    public SubtitleStyleSettings UpdateStyle(
        SubtitleStyleSettings current,
        double? sourceFontSize = null,
        double? translationFontSize = null,
        double? backgroundOpacity = null,
        double? bottomMargin = null,
        double? dualSpacing = null,
        string? sourceForegroundHex = null,
        string? translationForegroundHex = null)
    {
        return current with
        {
            SourceFontSize = sourceFontSize ?? current.SourceFontSize,
            TranslationFontSize = translationFontSize ?? current.TranslationFontSize,
            BackgroundOpacity = backgroundOpacity ?? current.BackgroundOpacity,
            BottomMargin = bottomMargin ?? current.BottomMargin,
            DualSpacing = dualSpacing ?? current.DualSpacing,
            SourceForegroundHex = sourceForegroundHex ?? current.SourceForegroundHex,
            TranslationForegroundHex = translationForegroundHex ?? current.TranslationForegroundHex
        };
    }

    public Task<IReadOnlyList<SubtitleCue>> LoadExternalSubtitleCuesAsync(
        string path,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        return SubtitleImportService.LoadExternalSubtitleCuesAsync(path, onRuntimeProgress, onStatus, cancellationToken);
    }

    public Task<IReadOnlyList<SubtitleCue>> ExtractEmbeddedSubtitleCuesAsync(
        string videoPath,
        MediaTrackInfo track,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        return SubtitleImportService.ExtractEmbeddedSubtitleCuesAsync(videoPath, track, onRuntimeProgress, onStatus, cancellationToken);
    }
}
