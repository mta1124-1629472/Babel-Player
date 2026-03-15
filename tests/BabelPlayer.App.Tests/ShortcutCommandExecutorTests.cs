using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

public sealed class ShortcutCommandExecutorTests
{
    // ── Shell-action-only commands ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Fullscreen_ReturnsToggleFullscreenAction()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync("fullscreen");

        Assert.Equal(ShortcutShellAction.ToggleFullscreen, result.ShellAction);
    }

    [Fact]
    public async Task ExecuteAsync_ExitFullscreen_ReturnsExitFullscreenAction()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync("exit_fullscreen");

        Assert.Equal(ShortcutShellAction.ExitFullscreen, result.ShellAction);
    }

    [Fact]
    public async Task ExecuteAsync_Pip_ReturnsPictureInPictureAction()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync("pip");

        Assert.Equal(ShortcutShellAction.TogglePictureInPicture, result.ShellAction);
    }

    [Fact]
    public async Task ExecuteAsync_SubtitleToggle_ReturnsToggleSubtitleVisibility()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync("subtitle_toggle");

        Assert.Equal(ShortcutShellAction.ToggleSubtitleVisibility, result.ShellAction);
    }

    // ── Overlay-interaction commands ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SeekForwardSmall_ReturnsRequiresOverlayInteraction()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync("seek_forward_small");

        Assert.True(result.RequiresOverlayInteraction);
    }

    [Fact]
    public async Task ExecuteAsync_SeekBackSmall_ReturnsRequiresOverlayInteraction()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync("seek_back_small");

        Assert.True(result.RequiresOverlayInteraction);
    }

    [Fact]
    public async Task ExecuteAsync_SeekForwardLarge_ReturnsRequiresOverlayInteraction()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync("seek_forward_large");

        Assert.True(result.RequiresOverlayInteraction);
    }

    [Fact]
    public async Task ExecuteAsync_SeekBackLarge_ReturnsRequiresOverlayInteraction()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync("seek_back_large");

        Assert.True(result.RequiresOverlayInteraction);
    }

    [Fact]
    public async Task ExecuteAsync_NextFrame_ReturnsRequiresOverlayInteractionWithStatusMessage()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync("next_frame");

        Assert.True(result.RequiresOverlayInteraction);
        Assert.NotNull(result.StatusMessage);
    }

    [Fact]
    public async Task ExecuteAsync_PreviousFrame_ReturnsRequiresOverlayInteractionWithStatusMessage()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync("previous_frame");

        Assert.True(result.RequiresOverlayInteraction);
        Assert.NotNull(result.StatusMessage);
    }

    // ── Queue navigation ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NextItem_ReturnsItemToLoadFromQueue()
    {
        var queue = new FakeQueueCommands();
        queue.NextItem = new ShellPlaylistItem { Path = "C:\\next.mp4" };
        var executor = CreateExecutor(queueCommands: queue);

        var result = await executor.ExecuteAsync("next_item");

        Assert.Equal("C:\\next.mp4", result.ItemToLoad?.Path);
    }

    [Fact]
    public async Task ExecuteAsync_PreviousItem_ReturnsItemToLoadFromQueue()
    {
        var queue = new FakeQueueCommands();
        queue.PreviousItem = new ShellPlaylistItem { Path = "C:\\prev.mp4" };
        var executor = CreateExecutor(queueCommands: queue);

        var result = await executor.ExecuteAsync("previous_item");

        Assert.Equal("C:\\prev.mp4", result.ItemToLoad?.Path);
    }

    [Fact]
    public async Task ExecuteAsync_NextItem_ReturnsNullItemToLoad_WhenQueueEmpty()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync("next_item");

        Assert.Null(result.ItemToLoad);
    }

    // ── Playback commands ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_PlayPause_ResumeWhenPaused()
    {
        var playback = new FakePlaybackCommands { IsPaused = true };
        var executor = CreateExecutor(playbackCommands: playback);

        var result = await executor.ExecuteAsync("play_pause");

        Assert.True(playback.PlayCalled);
        Assert.Contains("resumed", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_PlayPause_PauseWhenPlaying()
    {
        var playback = new FakePlaybackCommands { IsPaused = false };
        var executor = CreateExecutor(playbackCommands: playback);

        var result = await executor.ExecuteAsync("play_pause");

        Assert.True(playback.PauseCalled);
        Assert.Contains("paused", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── Speed commands ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SpeedReset_Sets1x()
    {
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync("speed_reset");

        Assert.Contains("1.00x", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_SpeedUp_IncreasesRateByQuarter()
    {
        var preferences = new FakeShellPreferencesService { PlaybackRate = 1.0 };
        var executor = CreateExecutor(preferencesService: preferences);

        var result = await executor.ExecuteAsync("speed_up");

        Assert.Contains("1.25x", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── Unknown command ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_UnknownCommand_ThrowsInvalidOperationException()
    {
        var executor = CreateExecutor();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync("totally_unknown_command_xyz"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ShortcutCommandExecutor CreateExecutor(
        FakeQueueCommands? queueCommands = null,
        FakePlaybackCommands? playbackCommands = null,
        FakeShellPreferencesService? preferencesService = null,
        FakeShellPreferenceCommands? preferenceCommands = null,
        FakeSubtitleWorkflowService? subtitleService = null)
    {
        return new ShortcutCommandExecutor(
            queueCommands ?? new FakeQueueCommands(),
            playbackCommands ?? new FakePlaybackCommands(),
            preferencesService ?? new FakeShellPreferencesService(),
            preferenceCommands ?? new FakeShellPreferenceCommands(),
            subtitleService ?? new FakeSubtitleWorkflowService());
    }

    private sealed class FakeQueueCommands : IQueueCommands
    {
        public ShellPlaylistItem? NextItem { get; set; }
        public ShellPlaylistItem? PreviousItem { get; set; }

        public ShellQueueMediaResult EnqueueFiles(IEnumerable<string> files, bool autoplay) => new();
        public ShellQueueMediaResult EnqueueFolder(string folderPath, bool autoplay) => new();
        public ShellQueueMediaResult EnqueueDroppedItems(IEnumerable<string> files, IEnumerable<string> folders) => new();
        public ShellQueueMediaResult PlayNow(string path) => new();
        public ShellQueueMediaResult PlayNext(string path) => new();
        public ShellQueueMediaResult AddToQueue(IEnumerable<string> files) => new();
        public ShellQueueMediaResult AddDroppedItemsToQueue(IEnumerable<string> files, IEnumerable<string> folders) => new();
        public ShellPlaylistItem? MovePrevious() => PreviousItem;
        public ShellPlaylistItem? MoveNext() => NextItem;
        public ShellQueueMediaResult ReorderQueueItem(int sourceIndex, int? targetIndex) => new();
        public void RemoveQueueItemAt(int index) { }
        public void ClearQueue() { }
    }

    private sealed class FakePlaybackCommands : IShellPlaybackCommands
    {
        public bool IsPaused { get; set; } = true;
        public bool PlayCalled { get; private set; }
        public bool PauseCalled { get; private set; }

        public ShellPlaybackStateSnapshot CurrentPlaybackSnapshot => new() { IsPaused = IsPaused };

        public Task<bool> LoadPlaybackItemAsync(ShellPlaylistItem? item, ShellLoadMediaOptions options, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<ShellPlaybackOpenResult> HandleMediaOpenedAsync(ShellPlaybackStateSnapshot snapshot, ShellPreferencesSnapshot preferences, CancellationToken cancellationToken = default) => Task.FromResult(new ShellPlaybackOpenResult());
        public Task<ShellResumeDecisionResult> ApplyResumeDecisionAsync(ShellResumeDecision decision, CancellationToken cancellationToken = default) => Task.FromResult(new ShellResumeDecisionResult());
        public ShellMediaEndedResult HandleMediaEnded(bool resumeEnabled, bool autoPlayNextInQueue = true) => new();
        public Task PlayAsync(CancellationToken cancellationToken = default) { PlayCalled = true; return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { PauseCalled = true; return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekRelativeAsync(TimeSpan delta, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StepFrameAsync(bool forward, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplyAudioPreferencesAsync(double volume, bool muted, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplyPlaybackDefaultsAsync(ShellPlaybackDefaultsChange change, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ShellSubtitleTrackSelectionResult> SelectEmbeddedSubtitleTrackAsync(string? currentPath, SubtitlePipelineSource currentSubtitleSource, ShellMediaTrack? track, CancellationToken cancellationToken = default) => Task.FromResult(new ShellSubtitleTrackSelectionResult());
        public Task SetAudioDelayAsync(double seconds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAspectRatioAsync(string aspectRatio, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetHardwareDecodingModeAsync(ShellHardwareDecodingMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetResumeTrackingEnabled(bool enabled) { }
        public void ClearResumeHistory() { }
        public void FlushResumeTracking(bool forceRemoveCompleted = false) { }
        public Task<ShellWorkflowTransitionResult> PrepareForTranscriptionRefreshAsync(SubtitleWorkflowSnapshot snapshot, ShellPlaybackStateSnapshot playbackState, CancellationToken cancellationToken = default) => Task.FromResult(new ShellWorkflowTransitionResult());
        public Task<ShellWorkflowTransitionResult> EvaluateCaptionStartupGateAsync(SubtitleWorkflowSnapshot snapshot, ShellPlaybackStateSnapshot playbackState, CancellationToken cancellationToken = default) => Task.FromResult(new ShellWorkflowTransitionResult());
    }

    private sealed class FakeShellPreferencesService : IShellPreferencesService
    {
        public double PlaybackRate { get; set; } = 1.0;
        public bool IsMuted { get; set; }
        public double VolumeLevel { get; set; } = 0.8;
        public double AudioDelaySeconds { get; set; }
        public double SubtitleDelaySeconds { get; set; }
        public string AspectRatio { get; set; } = "auto";
        public ShellHardwareDecodingMode HardwareDecodingMode { get; set; }

        public event Action<ShellPreferencesSnapshot>? SnapshotChanged { add { } remove { } }

        public ShellPreferencesSnapshot Current => new()
        {
            PlaybackRate = PlaybackRate,
            IsMuted = IsMuted,
            VolumeLevel = VolumeLevel,
            AudioDelaySeconds = AudioDelaySeconds,
            SubtitleDelaySeconds = SubtitleDelaySeconds,
            AspectRatio = AspectRatio,
            HardwareDecodingMode = HardwareDecodingMode
        };

        public ShellPreferencesSnapshot ApplyLayoutChange(ShellLayoutPreferencesChange change) => Current;
        public ShellPreferencesSnapshot ApplyPlaybackDefaultsChange(ShellPlaybackDefaultsChange change) => Current;
        public ShellPreferencesSnapshot ApplySubtitlePresentationChange(ShellSubtitlePresentationChange change) => Current;
        public ShellPreferencesSnapshot ApplyAudioStateChange(ShellAudioStateChange change) => Current;
        public ShellPreferencesSnapshot ApplyShortcutProfileChange(ShellShortcutProfileChange change) => Current;
        public ShellPreferencesSnapshot ApplyResumeEnabledChange(ShellResumeEnabledChange change) => Current;
        public ShellPreferencesSnapshot ApplyAutoPlayNextInQueueChange(ShellAutoPlayNextInQueueChange change) => Current;
        public ShellPreferencesSnapshot ApplyPinnedRootsChange(ShellPinnedRootsChange change) => Current;
    }

    private sealed class FakeShellPreferenceCommands : IShellPreferenceCommands
    {
        public ShellPreferencesSnapshot ApplyLayoutChange(ShellLayoutPreferencesChange change) => new();

        public Task<ShellPreferencesSnapshot> ApplyAudioStateAsync(ShellAudioStateChange change, CancellationToken cancellationToken = default)
            => Task.FromResult(new ShellPreferencesSnapshot());

        public Task<ShellPreferencesSnapshot> ApplyPlaybackDefaultsAsync(ShellPlaybackDefaultsChange change, CancellationToken cancellationToken = default)
            => Task.FromResult(new ShellPreferencesSnapshot { PlaybackRate = change.PlaybackRate });

        public ShellPreferencesSnapshot ApplySubtitlePresentationChange(ShellSubtitlePresentationChange change) => new();

        public ShellPreferencesSnapshot ApplyShortcutProfileChange(ShellShortcutProfile profile) => new();

        public ShellResumePreferenceResult ApplyResumeEnabledChange(bool enabled) => new(new ShellPreferencesSnapshot(), false);

        public ShellPreferencesSnapshot ApplyAutoPlayNextInQueueChange(bool enabled) => new();
    }

    private sealed class FakeSubtitleWorkflowService : ISubtitleWorkflowShellService
    {
        public bool IsTranslationEnabled { get; private set; }

        public event Action<SubtitleWorkflowSnapshot>? SnapshotChanged { add { } remove { } }
        public event Action<string>? StatusChanged { add { } remove { } }

        public SubtitleWorkflowSnapshot Current => new() { IsTranslationEnabled = IsTranslationEnabled };

        public bool HasCurrentCues => false;

        public ShellSubtitleRenderMode GetEffectiveRenderMode(ShellSubtitleRenderMode requestedMode, bool sourceOnlyOverrideForCurrentVideo = false) => requestedMode;

        public SubtitleOverlayPresentation GetOverlayPresentation(ShellSubtitleRenderMode requestedMode, bool subtitlesVisible = true, bool sourceOnlyOverrideForCurrentVideo = false) => new();

        public SubtitleRenderModeCommandResult SelectRenderMode(ShellSubtitleRenderMode selectedMode, ShellSubtitleRenderMode currentRequestedMode) => new(selectedMode, selectedMode);

        public SubtitleRenderModeCommandResult ToggleSubtitleVisibility(ShellSubtitleRenderMode currentRequestedMode) => new(currentRequestedMode, currentRequestedMode);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> SelectTranscriptionModelAsync(string modelKey, CancellationToken cancellationToken = default, bool suppressStatus = false) => Task.FromResult(true);

        public Task<bool> SelectTranslationModelAsync(string modelKey, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task SetTranslationEnabledAsync(bool enabled, bool lockPreference = true, CancellationToken cancellationToken = default)
        {
            IsTranslationEnabled = enabled;
            return Task.CompletedTask;
        }

        public Task SetAutoTranslateEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SubtitleLoadResult> ImportExternalSubtitlesAsync(string path, bool autoLoaded = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new SubtitleLoadResult(SubtitlePipelineSource.None, 0, false, false));

        public void ExportCurrentSubtitles(string path) { }

        public void Dispose() { }
    }
}
