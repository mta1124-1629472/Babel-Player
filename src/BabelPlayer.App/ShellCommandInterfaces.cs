namespace BabelPlayer.App;

public interface IQueueProjectionReader
{
    event Action<ShellQueueSnapshot>? QueueSnapshotChanged;

    ShellQueueSnapshot QueueSnapshot { get; }

    ShellPlaylistItem? NowPlayingItem { get; }
}

public interface IQueueCommands
{
    ShellQueueMediaResult EnqueueFiles(IEnumerable<string> files, bool autoplay);

    ShellQueueMediaResult EnqueueFolder(string folderPath, bool autoplay);

    ShellQueueMediaResult EnqueueDroppedItems(IEnumerable<string> files, IEnumerable<string> folders);

    ShellQueueMediaResult PlayNow(string path);

    ShellQueueMediaResult PlayNext(string path);

    ShellQueueMediaResult AddToQueue(IEnumerable<string> files);

    ShellQueueMediaResult AddDroppedItemsToQueue(IEnumerable<string> files, IEnumerable<string> folders);

    ShellPlaylistItem? MovePrevious();

    ShellPlaylistItem? MoveNext();

    ShellQueueMediaResult ReorderQueueItem(int sourceIndex, int? targetIndex);

    void RemoveQueueItemAt(int index);

    void ClearQueue();
}

public interface IShellPlaybackCommands
{
    ShellPlaybackStateSnapshot CurrentPlaybackSnapshot { get; }

    Task<bool> LoadPlaybackItemAsync(
        ShellPlaylistItem? item,
        ShellLoadMediaOptions options,
        CancellationToken cancellationToken);

    Task<ShellPlaybackOpenResult> HandleMediaOpenedAsync(
        ShellPlaybackStateSnapshot snapshot,
        ShellPreferencesSnapshot preferences,
        CancellationToken cancellationToken = default);

    ShellMediaEndedResult HandleMediaEnded(bool resumeEnabled);

    Task PlayAsync(CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    Task SeekRelativeAsync(TimeSpan delta, CancellationToken cancellationToken = default);

    Task StepFrameAsync(bool forward, CancellationToken cancellationToken = default);

    Task ApplyAudioPreferencesAsync(double volume, bool muted, CancellationToken cancellationToken = default);

    Task ApplyPlaybackDefaultsAsync(ShellPlaybackDefaultsChange change, CancellationToken cancellationToken = default);

    Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken = default);

    Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken = default);

    Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken = default);

    Task<ShellSubtitleTrackSelectionResult> SelectEmbeddedSubtitleTrackAsync(
        string? currentPath,
        SubtitlePipelineSource currentSubtitleSource,
        ShellMediaTrack? track,
        CancellationToken cancellationToken = default);

    Task SetAudioDelayAsync(double seconds, CancellationToken cancellationToken = default);

    Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken = default);

    Task SetAspectRatioAsync(string aspectRatio, CancellationToken cancellationToken = default);

    Task SetHardwareDecodingModeAsync(ShellHardwareDecodingMode mode, CancellationToken cancellationToken = default);

    void SetResumeTrackingEnabled(bool enabled);

    void ClearResumeHistory();

    void FlushResumeTracking(bool forceRemoveCompleted = false);

    Task<ShellWorkflowTransitionResult> PrepareForTranscriptionRefreshAsync(
        SubtitleWorkflowSnapshot snapshot,
        ShellPlaybackStateSnapshot playbackState,
        CancellationToken cancellationToken = default);

    Task<ShellWorkflowTransitionResult> EvaluateCaptionStartupGateAsync(
        SubtitleWorkflowSnapshot snapshot,
        ShellPlaybackStateSnapshot playbackState,
        CancellationToken cancellationToken = default);
}

public interface ISubtitleWorkflowShellService : IDisposable
{
    event Action<SubtitleWorkflowSnapshot>? SnapshotChanged;

    event Action<string>? StatusChanged;

    SubtitleWorkflowSnapshot Current { get; }

    bool HasCurrentCues { get; }

    ShellSubtitleRenderMode GetEffectiveRenderMode(
        ShellSubtitleRenderMode requestedMode,
        bool sourceOnlyOverrideForCurrentVideo = false);

    SubtitleOverlayPresentation GetOverlayPresentation(
        ShellSubtitleRenderMode requestedMode,
        bool subtitlesVisible = true,
        bool sourceOnlyOverrideForCurrentVideo = false);

    SubtitleRenderModeCommandResult SelectRenderMode(
        ShellSubtitleRenderMode selectedMode,
        ShellSubtitleRenderMode currentRequestedMode);

    SubtitleRenderModeCommandResult ToggleSubtitleVisibility(
        ShellSubtitleRenderMode currentRequestedMode);

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<bool> SelectTranscriptionModelAsync(
        string modelKey,
        CancellationToken cancellationToken = default,
        bool suppressStatus = false);

    Task<bool> SelectTranslationModelAsync(
        string modelKey,
        CancellationToken cancellationToken = default);

    Task SetTranslationEnabledAsync(
        bool enabled,
        bool lockPreference = true,
        CancellationToken cancellationToken = default);

    Task SetAutoTranslateEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<SubtitleLoadResult> ImportExternalSubtitlesAsync(
        string path,
        bool autoLoaded = false,
        CancellationToken cancellationToken = default);

    void ExportCurrentSubtitles(string path);
}

public sealed record SubtitleRenderModeCommandResult(
    ShellSubtitleRenderMode RequestedRenderMode,
    ShellSubtitleRenderMode EffectiveRenderMode);
