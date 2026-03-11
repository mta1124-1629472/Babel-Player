using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class SubtitleWorkflowController : IDisposable
{
    private readonly SubtitlePresentationProjector _subtitlePresentationProjector;
    private readonly SubtitleApplicationService _subtitleApplicationService;
    private readonly SubtitleWorkflowProjectionAdapter _projectionAdapter;
    private readonly IMediaSessionStore _mediaSessionStore;

    public SubtitleWorkflowController(
        SubtitleApplicationService subtitleApplicationService,
        SubtitleWorkflowProjectionAdapter projectionAdapter,
        SubtitlePresentationProjector subtitlePresentationProjector)
    {
        _subtitleApplicationService = subtitleApplicationService;
        _projectionAdapter = projectionAdapter;
        _subtitlePresentationProjector = subtitlePresentationProjector;
        _mediaSessionStore = _subtitleApplicationService.MediaSessionStore;
        _projectionAdapter.SnapshotChanged += HandleSnapshotChanged;
        _subtitleApplicationService.StatusChanged += HandleStatusChanged;
        _subtitleApplicationService.RuntimeInstallProgressChanged += HandleRuntimeInstallProgressChanged;
    }

    public event Action<SubtitleWorkflowSnapshot>? SnapshotChanged;
    public event Action<string>? StatusChanged;
    public event Action<RuntimeInstallProgress>? RuntimeInstallProgressChanged;

    public IMediaSessionStore MediaSessionStore => _mediaSessionStore;

    public SubtitleWorkflowSnapshot Snapshot => _projectionAdapter.Current;

    public IReadOnlyList<SubtitleCue> CurrentCues => _subtitleApplicationService.CurrentCues;

    public bool HasCurrentCues => _subtitleApplicationService.HasCurrentCues;

    public SubtitleOverlayPresentation GetOverlayPresentation(
        SubtitleRenderMode renderMode,
        bool subtitlesVisible = true,
        bool sourceOnlyOverrideForCurrentVideo = false)
    {
        var presentation = _subtitlePresentationProjector.Build(
            _mediaSessionStore.Snapshot,
            renderMode,
            subtitlesVisible,
            sourceOnlyOverrideForCurrentVideo);
        return new SubtitleOverlayPresentation
        {
            IsVisible = presentation.IsVisible,
            PrimaryText = presentation.PrimaryText,
            SecondaryText = presentation.SecondaryText
        };
    }

    public SubtitleRenderMode GetEffectiveRenderMode(
        SubtitleRenderMode requestedMode,
        bool sourceOnlyOverrideForCurrentVideo = false)
    {
        return _subtitlePresentationProjector.GetEffectiveRenderMode(
            _mediaSessionStore.Snapshot,
            requestedMode,
            sourceOnlyOverrideForCurrentVideo);
    }

    public void ExportCurrentSubtitles(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SubtitleFileService.ExportSrt(path, CurrentCues);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _subtitleApplicationService.InitializeAsync(cancellationToken);

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

    public Task<bool> SelectTranscriptionModelAsync(string modelKey, CancellationToken cancellationToken = default, bool suppressStatus = false)
        => _subtitleApplicationService.SelectTranscriptionModelAsync(modelKey, cancellationToken, suppressStatus);

    public Task<bool> SelectTranslationModelAsync(string modelKey, CancellationToken cancellationToken = default)
        => _subtitleApplicationService.SelectTranslationModelAsync(modelKey, cancellationToken);

    public Task SetTranslationEnabledAsync(bool enabled, bool lockPreference = true, CancellationToken cancellationToken = default)
        => _subtitleApplicationService.SetTranslationEnabledAsync(enabled, lockPreference, cancellationToken);

    public Task SetAutoTranslateEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        => _subtitleApplicationService.SetAutoTranslateEnabledAsync(enabled, cancellationToken);

    public Task<SubtitleLoadResult> LoadMediaSubtitlesAsync(string videoPath, CancellationToken cancellationToken = default)
        => _subtitleApplicationService.LoadMediaSubtitlesAsync(videoPath, cancellationToken);

    public Task<SubtitleLoadResult> ImportExternalSubtitlesAsync(string path, bool autoLoaded = false, CancellationToken cancellationToken = default)
        => _subtitleApplicationService.ImportExternalSubtitlesAsync(path, autoLoaded, cancellationToken);

    public Task<SubtitleLoadResult> ImportEmbeddedSubtitleTrackAsync(string videoPath, MediaTrackInfo track, CancellationToken cancellationToken = default)
        => _subtitleApplicationService.ImportEmbeddedSubtitleTrackAsync(videoPath, track, cancellationToken);

    public void Dispose()
    {
        _projectionAdapter.SnapshotChanged -= HandleSnapshotChanged;
        _subtitleApplicationService.StatusChanged -= HandleStatusChanged;
        _subtitleApplicationService.RuntimeInstallProgressChanged -= HandleRuntimeInstallProgressChanged;
        _projectionAdapter.Dispose();
        _subtitleApplicationService.Dispose();
    }

    private void HandleSnapshotChanged(SubtitleWorkflowSnapshot snapshot)
    {
        SnapshotChanged?.Invoke(snapshot);
    }

    private void HandleStatusChanged(string message)
    {
        StatusChanged?.Invoke(message);
    }

    private void HandleRuntimeInstallProgressChanged(RuntimeInstallProgress progress)
    {
        RuntimeInstallProgressChanged?.Invoke(progress);
    }
}
