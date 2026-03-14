using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class SubtitleWorkflowController : ISubtitleWorkflowShellService
{
    private readonly SubtitlePresentationProjector _subtitlePresentationProjector;
    private readonly SubtitleApplicationService _subtitleApplicationService;
    private readonly SubtitleWorkflowProjectionAdapter _projectionAdapter;
    private readonly IMediaSessionStore _mediaSessionStore;
    private string? _sourceOnlyOverrideVideoPath;
    private ShellSubtitleRenderMode _lastVisibilityRestoreMode = ShellSubtitleRenderMode.TranslationOnly;
    private ShellSubtitleRenderMode _lastRequestedNonOffRenderMode = ShellSubtitleRenderMode.TranslationOnly;

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
    public event Action<ShellRuntimeInstallProgress>? RuntimeInstallProgressChanged;

    public IMediaSessionStore MediaSessionStore => _mediaSessionStore;

    public SubtitleWorkflowSnapshot Current => _projectionAdapter.Current;

    public SubtitleWorkflowSnapshot Snapshot => Current;

    public IReadOnlyList<ShellSubtitleCue> CurrentCues => _subtitleApplicationService.CurrentCues.Select(cue => cue.ToShell()).ToArray();

    public bool HasCurrentCues => _subtitleApplicationService.HasCurrentCues;

    public SubtitleOverlayPresentation GetOverlayPresentation(
        ShellSubtitleRenderMode renderMode,
        bool subtitlesVisible = true,
        bool sourceOnlyOverrideForCurrentVideo = false)
    {
        SyncPolicy(renderMode);
        var presentation = _subtitlePresentationProjector.Build(
            _mediaSessionStore.Snapshot,
            renderMode.ToCore(),
            subtitlesVisible,
            sourceOnlyOverrideForCurrentVideo || HasSourceOnlyOverrideForCurrentVideo(Current));
        return new SubtitleOverlayPresentation
        {
            IsVisible = presentation.IsVisible,
            PrimaryText = presentation.PrimaryText,
            SecondaryText = presentation.SecondaryText
        };
    }

    public ShellSubtitleRenderMode GetEffectiveRenderMode(
        ShellSubtitleRenderMode requestedMode,
        bool sourceOnlyOverrideForCurrentVideo = false)
    {
        SyncPolicy(requestedMode);
        return ComputeEffectiveRenderMode(
            requestedMode,
            sourceOnlyOverrideForCurrentVideo || HasSourceOnlyOverrideForCurrentVideo(Current));
    }

    public SubtitleRenderModeCommandResult SelectRenderMode(
        ShellSubtitleRenderMode selectedMode,
        ShellSubtitleRenderMode currentRequestedMode)
    {
        SyncPolicy(currentRequestedMode);
        if (selectedMode != ShellSubtitleRenderMode.Off)
        {
            _lastVisibilityRestoreMode = selectedMode;
        }

        if (Current.IsTranslationEnabled
            && selectedMode is ShellSubtitleRenderMode.SourceOnly or ShellSubtitleRenderMode.TranscribeOnly
            && !string.IsNullOrWhiteSpace(Current.CurrentVideoPath))
        {
            _sourceOnlyOverrideVideoPath = Current.CurrentVideoPath;
            var requestedRenderMode = ResolvePersistedRenderModeForSourceOnly(currentRequestedMode);
            return new SubtitleRenderModeCommandResult(
                requestedRenderMode,
                GetEffectiveRenderMode(requestedRenderMode));
        }

        _sourceOnlyOverrideVideoPath = null;
        if (selectedMode != ShellSubtitleRenderMode.Off)
        {
            _lastRequestedNonOffRenderMode = selectedMode;
        }

        return new SubtitleRenderModeCommandResult(
            selectedMode,
            GetEffectiveRenderMode(selectedMode));
    }

    public SubtitleRenderModeCommandResult ToggleSubtitleVisibility(ShellSubtitleRenderMode currentRequestedMode)
    {
        SyncPolicy(currentRequestedMode);
        var currentEffectiveMode = GetEffectiveRenderMode(currentRequestedMode);
        if (currentEffectiveMode != ShellSubtitleRenderMode.Off)
        {
            _lastVisibilityRestoreMode = currentEffectiveMode;
            return new SubtitleRenderModeCommandResult(ShellSubtitleRenderMode.Off, ShellSubtitleRenderMode.Off);
        }

        var restoreMode = _lastVisibilityRestoreMode == ShellSubtitleRenderMode.Off
            ? _lastRequestedNonOffRenderMode
            : _lastVisibilityRestoreMode;
        if (restoreMode == ShellSubtitleRenderMode.Off)
        {
            restoreMode = ShellSubtitleRenderMode.TranslationOnly;
        }

        if (Current.IsTranslationEnabled
            && restoreMode is ShellSubtitleRenderMode.SourceOnly or ShellSubtitleRenderMode.TranscribeOnly
            && !string.IsNullOrWhiteSpace(Current.CurrentVideoPath))
        {
            _sourceOnlyOverrideVideoPath = Current.CurrentVideoPath;
            var requestedRenderMode = ResolvePersistedRenderModeForSourceOnly(currentRequestedMode);
            return new SubtitleRenderModeCommandResult(
                requestedRenderMode,
                GetEffectiveRenderMode(requestedRenderMode));
        }

        _sourceOnlyOverrideVideoPath = null;
        if (restoreMode != ShellSubtitleRenderMode.Off)
        {
            _lastRequestedNonOffRenderMode = restoreMode;
            _lastVisibilityRestoreMode = restoreMode;
        }

        return new SubtitleRenderModeCommandResult(
            restoreMode,
            GetEffectiveRenderMode(restoreMode));
    }

    public void ExportCurrentSubtitles(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SubtitleFileService.ExportSrt(path, CurrentCues.Select(cue => cue.ToCore()).ToArray());
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _subtitleApplicationService.InitializeAsync(cancellationToken);

    public ShellSubtitleRenderMode ToggleSource(ShellSubtitleRenderMode current)
    {
        return current switch
        {
            ShellSubtitleRenderMode.Off => ShellSubtitleRenderMode.TranscribeOnly,
            ShellSubtitleRenderMode.SourceOnly => ShellSubtitleRenderMode.Off,
            ShellSubtitleRenderMode.TranscribeOnly => ShellSubtitleRenderMode.Off,
            ShellSubtitleRenderMode.TranslationOnly => ShellSubtitleRenderMode.Dual,
            ShellSubtitleRenderMode.Dual => ShellSubtitleRenderMode.TranslationOnly,
            _ => ShellSubtitleRenderMode.TranslationOnly
        };
    }

    public ShellSubtitleRenderMode ToggleTranslation(ShellSubtitleRenderMode current)
    {
        return current switch
        {
            ShellSubtitleRenderMode.Off => ShellSubtitleRenderMode.TranslationOnly,
            ShellSubtitleRenderMode.SourceOnly => ShellSubtitleRenderMode.Dual,
            ShellSubtitleRenderMode.TranscribeOnly => ShellSubtitleRenderMode.Dual,
            ShellSubtitleRenderMode.TranslationOnly => ShellSubtitleRenderMode.Off,
            ShellSubtitleRenderMode.Dual => ShellSubtitleRenderMode.SourceOnly,
            _ => ShellSubtitleRenderMode.TranslationOnly
        };
    }

    public ShellSubtitleStyle UpdateStyle(
        ShellSubtitleStyle current,
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
        ClearSourceOnlyOverrideIfInactive(snapshot);
        SnapshotChanged?.Invoke(snapshot);
    }

    private void HandleStatusChanged(string message)
    {
        StatusChanged?.Invoke(message);
    }

    private void HandleRuntimeInstallProgressChanged(RuntimeInstallProgress progress)
    {
        RuntimeInstallProgressChanged?.Invoke(progress.ToShell());
    }

    private void SyncPolicy(ShellSubtitleRenderMode requestedMode)
    {
        ClearSourceOnlyOverrideIfInactive(Current);
        if (requestedMode != ShellSubtitleRenderMode.Off)
        {
            _lastRequestedNonOffRenderMode = requestedMode;
            _lastVisibilityRestoreMode = ComputeEffectiveRenderMode(
                requestedMode,
                HasSourceOnlyOverrideForCurrentVideo(Current));
        }
    }

    private ShellSubtitleRenderMode ResolvePersistedRenderModeForSourceOnly(ShellSubtitleRenderMode currentRequestedMode)
    {
        if (currentRequestedMode != ShellSubtitleRenderMode.Off)
        {
            _lastRequestedNonOffRenderMode = currentRequestedMode;
            return currentRequestedMode;
        }

        return _lastRequestedNonOffRenderMode == ShellSubtitleRenderMode.Off
            ? ShellSubtitleRenderMode.TranslationOnly
            : _lastRequestedNonOffRenderMode;
    }

    private bool HasSourceOnlyOverrideForCurrentVideo(SubtitleWorkflowSnapshot snapshot)
    {
        return !string.IsNullOrWhiteSpace(_sourceOnlyOverrideVideoPath)
            && !string.IsNullOrWhiteSpace(snapshot.CurrentVideoPath)
            && string.Equals(_sourceOnlyOverrideVideoPath, snapshot.CurrentVideoPath, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearSourceOnlyOverrideIfInactive(SubtitleWorkflowSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(_sourceOnlyOverrideVideoPath))
        {
            return;
        }

        if (!snapshot.IsTranslationEnabled
            || string.IsNullOrWhiteSpace(snapshot.CurrentVideoPath)
            || !string.Equals(_sourceOnlyOverrideVideoPath, snapshot.CurrentVideoPath, StringComparison.OrdinalIgnoreCase))
        {
            _sourceOnlyOverrideVideoPath = null;
        }
    }

    private ShellSubtitleRenderMode ComputeEffectiveRenderMode(
        ShellSubtitleRenderMode requestedMode,
        bool sourceOnlyOverrideForCurrentVideo)
    {
        return _subtitlePresentationProjector.GetEffectiveRenderMode(
            _mediaSessionStore.Snapshot,
            requestedMode.ToCore(),
            sourceOnlyOverrideForCurrentVideo).ToShell();
    }
}
