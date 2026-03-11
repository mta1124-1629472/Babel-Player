using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed record ShellLoadMediaOptions
{
    public HardwareDecodingMode HardwareDecodingMode { get; init; } = HardwareDecodingMode.AutoSafe;
    public double PlaybackRate { get; init; } = 1.0;
    public string AspectRatio { get; init; } = "auto";
    public double AudioDelaySeconds { get; init; }
    public double SubtitleDelaySeconds { get; init; }
    public double Volume { get; init; } = 0.8;
    public bool ResumeEnabled { get; init; }
    public PlaybackStateSnapshot PreviousPlaybackState { get; init; } = new();
}

public sealed record ShellQueueMediaResult
{
    public IReadOnlyList<PlaylistItem> AddedItems { get; init; } = [];
    public PlaylistItem? ItemToLoad { get; init; }
    public IReadOnlyList<string> PinnedFolders { get; init; } = [];
    public string? StatusMessage { get; init; }
    public bool IsError { get; init; }
}

public sealed record ShellPlaybackOpenResult
{
    public TimeSpan? ResumePosition { get; init; }
    public string StatusMessage { get; init; } = "Media opened.";
}

public sealed record ShellMediaEndedResult
{
    public PlaylistItem? NextItem { get; init; }
    public string StatusMessage { get; init; } = "Playback ended.";
}

public sealed record ShellWorkflowTransitionResult
{
    public string? StatusMessage { get; init; }
}

public sealed class ShellController
{
    private readonly PlaylistController _playlistController;
    private readonly PlaybackSessionController _playbackSessionController;
    private readonly IPlaybackBackend _playbackBackend;
    private readonly SubtitleWorkflowController _subtitleWorkflowController;
    private readonly LibraryBrowserService _libraryBrowserService;
    private readonly ResumePlaybackService _resumePlaybackService;

    private bool _autoResumePlaybackAfterCaptionReady;
    private string? _autoResumePlaybackPath;
    private TimeSpan _autoResumePlaybackPosition = TimeSpan.Zero;
    private bool _autoResumePlaybackFromBeginning = true;

    public ShellController(
        PlaylistController playlistController,
        PlaybackSessionController playbackSessionController,
        IPlaybackBackend playbackBackend,
        SubtitleWorkflowController subtitleWorkflowController,
        LibraryBrowserService? libraryBrowserService = null,
        ResumePlaybackService? resumePlaybackService = null)
    {
        _playlistController = playlistController;
        _playbackSessionController = playbackSessionController;
        _playbackBackend = playbackBackend;
        _subtitleWorkflowController = subtitleWorkflowController;
        _libraryBrowserService = libraryBrowserService ?? new LibraryBrowserService();
        _resumePlaybackService = resumePlaybackService ?? new ResumePlaybackService();
    }

    public IReadOnlyList<PlaylistItem> PlaylistItems => _playlistController.Items;

    public int CurrentPlaylistIndex => _playlistController.CurrentIndex;

    public PlaylistItem? CurrentPlaylistItem => _playlistController.CurrentItem;

    public ShellQueueMediaResult EnqueueFiles(IEnumerable<string> files, bool autoplay)
    {
        var added = _playlistController.EnqueueFiles(files);
        return new ShellQueueMediaResult
        {
            AddedItems = added,
            ItemToLoad = autoplay ? added.FirstOrDefault() : null
        };
    }

    public ShellQueueMediaResult EnqueueFolder(string folderPath, bool autoplay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var files = _libraryBrowserService.EnumerateMediaFiles(folderPath, recursive: true)
            .Where(path => !_playlistController.Items.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (files.Count == 0)
        {
            return new ShellQueueMediaResult
            {
                StatusMessage = $"No new supported media files were found in {folderPath}.",
                IsError = true
            };
        }

        var added = _playlistController.EnqueueFolder(folderPath, files);
        return new ShellQueueMediaResult
        {
            AddedItems = added,
            ItemToLoad = autoplay ? added.FirstOrDefault() : null,
            PinnedFolders = [folderPath],
            StatusMessage = $"Queued {added.Count} item(s) from {Path.GetFileName(folderPath)}."
        };
    }

    public ShellQueueMediaResult EnqueueDroppedItems(IEnumerable<string> files, IEnumerable<string> folders)
    {
        List<string> discoveredFiles = [];
        List<string> pinnedFolders = [];

        foreach (var file in files.Where(LibraryBrowserService.IsSupportedMediaFile))
        {
            discoveredFiles.Add(file);
        }

        foreach (var folder in folders.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            pinnedFolders.Add(folder);
            discoveredFiles.AddRange(_libraryBrowserService.EnumerateMediaFiles(folder, recursive: true));
        }

        if (discoveredFiles.Count == 0)
        {
            return new ShellQueueMediaResult
            {
                StatusMessage = "Dropped items did not contain supported media files.",
                IsError = true
            };
        }

        var added = _playlistController.EnqueueFiles(discoveredFiles);
        return new ShellQueueMediaResult
        {
            AddedItems = added,
            ItemToLoad = added.FirstOrDefault(),
            PinnedFolders = pinnedFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    public PlaylistItem? EnsurePlaylistItem(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var existing = _playlistController.Items.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        return existing ?? _playlistController.EnqueueFiles([path]).FirstOrDefault();
    }

    public PlaylistItem? MovePrevious() => _playlistController.MovePrevious();

    public PlaylistItem? MoveNext() => _playlistController.MoveNext();

    public void RemovePlaylistItemAt(int index)
    {
        _playlistController.RemoveAt(index);
    }

    public void ClearPlaylist()
    {
        _playlistController.Clear();
    }

    public async Task<bool> LoadPlaylistItemAsync(
        PlaylistItem? item,
        ShellLoadMediaOptions options,
        CancellationToken cancellationToken)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
        {
            return false;
        }

        ResetCaptionStartupGate();

        if (options.ResumeEnabled)
        {
            _resumePlaybackService.Update(options.PreviousPlaybackState);
        }

        _playbackSessionController.StartWith(item);

        await _playbackBackend.LoadAsync(item.Path, cancellationToken);
        await _playbackBackend.SetHardwareDecodingModeAsync(options.HardwareDecodingMode, cancellationToken);
        await _playbackBackend.SetPlaybackRateAsync(options.PlaybackRate, cancellationToken);
        await _playbackBackend.SetAspectRatioAsync(options.AspectRatio, cancellationToken);
        await _playbackBackend.SetAudioDelayAsync(options.AudioDelaySeconds, cancellationToken);
        await _playbackBackend.SetSubtitleDelayAsync(options.SubtitleDelaySeconds, cancellationToken);
        await _playbackBackend.SetZoomAsync(0, cancellationToken);
        await _playbackBackend.SetPanAsync(0, 0, cancellationToken);
        await _playbackBackend.SetVolumeAsync(options.Volume, cancellationToken);
        await _subtitleWorkflowController.LoadMediaSubtitlesAsync(item.Path, cancellationToken);
        return true;
    }

    public async Task<ShellPlaybackOpenResult> HandleMediaOpenedAsync(string? path, TimeSpan duration, bool resumeEnabled, CancellationToken cancellationToken = default)
    {
        var current = _playlistController.CurrentItem;
        var result = new ShellPlaybackOpenResult
        {
            StatusMessage = current is null ? "Media opened." : $"Now playing {current.DisplayName}."
        };

        if (!resumeEnabled)
        {
            return result;
        }

        var entry = _resumePlaybackService.FindEntry(path, duration);
        if (entry is null)
        {
            return result;
        }

        var resumePosition = TimeSpan.FromSeconds(Math.Clamp(entry.PositionSeconds, 0, duration.TotalSeconds));
        await _playbackBackend.SeekAsync(resumePosition, cancellationToken);
        return result with
        {
            ResumePosition = resumePosition
        };
    }

    public void SaveResumePosition(PlaybackStateSnapshot snapshot, bool enabled, bool forceRemoveCompleted = false)
    {
        if (!enabled)
        {
            return;
        }

        _resumePlaybackService.Update(snapshot, forceRemoveCompleted);
    }

    public void ClearResumeHistory()
    {
        _resumePlaybackService.Clear();
    }

    public ShellMediaEndedResult HandleMediaEnded(PlaybackStateSnapshot snapshot, bool resumeEnabled)
    {
        if (resumeEnabled)
        {
            _resumePlaybackService.Update(snapshot, forceRemoveCompleted: true);
        }

        ResetCaptionStartupGate();
        var next = _playlistController.AdvanceAfterMediaEnded();
        return new ShellMediaEndedResult
        {
            NextItem = next,
            StatusMessage = next is null ? "Playback ended." : $"Now playing {next.DisplayName}."
        };
    }

    public Task PlayAsync(CancellationToken cancellationToken = default)
        => _playbackBackend.PlayAsync(cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default)
        => _playbackBackend.PauseAsync(cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        => _playbackBackend.SeekAsync(position, cancellationToken);

    public Task SeekRelativeAsync(TimeSpan delta, CancellationToken cancellationToken = default)
        => _playbackBackend.SeekRelativeAsync(delta, cancellationToken);

    public Task StepFrameAsync(bool forward, CancellationToken cancellationToken = default)
        => _playbackBackend.StepFrameAsync(forward, cancellationToken);

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
        => _playbackBackend.SetVolumeAsync(volume, cancellationToken);

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
        => _playbackBackend.SetMuteAsync(muted, cancellationToken);

    public Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken = default)
        => _playbackBackend.SetPlaybackRateAsync(speed, cancellationToken);

    public Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken = default)
        => _playbackBackend.SetAudioTrackAsync(trackId, cancellationToken);

    public Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken = default)
        => _playbackBackend.SetSubtitleTrackAsync(trackId, cancellationToken);

    public Task SetAudioDelayAsync(double seconds, CancellationToken cancellationToken = default)
        => _playbackBackend.SetAudioDelayAsync(seconds, cancellationToken);

    public Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken = default)
        => _playbackBackend.SetSubtitleDelayAsync(seconds, cancellationToken);

    public Task SetAspectRatioAsync(string aspectRatio, CancellationToken cancellationToken = default)
        => _playbackBackend.SetAspectRatioAsync(aspectRatio, cancellationToken);

    public Task SetHardwareDecodingModeAsync(HardwareDecodingMode mode, CancellationToken cancellationToken = default)
        => _playbackBackend.SetHardwareDecodingModeAsync(mode, cancellationToken);

    public Task<ShellWorkflowTransitionResult> PrepareForTranscriptionRefreshAsync(
        SubtitleWorkflowSnapshot snapshot,
        PlaybackStateSnapshot playbackState,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.SubtitleSource != SubtitlePipelineSource.Generated || string.IsNullOrWhiteSpace(playbackState.Path))
        {
            return Task.FromResult(new ShellWorkflowTransitionResult());
        }

        _autoResumePlaybackAfterCaptionReady = true;
        _autoResumePlaybackPath = playbackState.Path;
        _autoResumePlaybackPosition = playbackState.Position;
        _autoResumePlaybackFromBeginning = false;
        return PauseForWorkflowTransitionAsync("Refreshing captions for the selected transcription model.", cancellationToken);
    }

    public async Task<ShellWorkflowTransitionResult> EvaluateCaptionStartupGateAsync(
        SubtitleWorkflowSnapshot snapshot,
        PlaybackStateSnapshot playbackState,
        CancellationToken cancellationToken = default)
    {
        var currentPath = playbackState.Path;
        if (string.IsNullOrWhiteSpace(currentPath) || !string.Equals(snapshot.CurrentVideoPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            ResetCaptionStartupGate();
            return new ShellWorkflowTransitionResult();
        }

        var shouldPauseForInitialCaptions = snapshot.SubtitleSource == SubtitlePipelineSource.Generated
            && snapshot.IsCaptionGenerationInProgress
            && snapshot.Cues.Count == 0
            && playbackState.Position <= TimeSpan.FromSeconds(2);

        if (shouldPauseForInitialCaptions && !_autoResumePlaybackAfterCaptionReady)
        {
            _autoResumePlaybackAfterCaptionReady = true;
            _autoResumePlaybackPath = currentPath;
            _autoResumePlaybackPosition = TimeSpan.Zero;
            _autoResumePlaybackFromBeginning = true;
            return await PauseForWorkflowTransitionAsync("Generating initial captions before playback starts.", cancellationToken);
        }

        if (_autoResumePlaybackAfterCaptionReady
            && string.Equals(_autoResumePlaybackPath, currentPath, StringComparison.OrdinalIgnoreCase)
            && snapshot.Cues.Count > 0)
        {
            _autoResumePlaybackAfterCaptionReady = false;
            _autoResumePlaybackPath = null;
            var resumePosition = _autoResumePlaybackFromBeginning ? TimeSpan.Zero : _autoResumePlaybackPosition;
            _autoResumePlaybackPosition = TimeSpan.Zero;
            _autoResumePlaybackFromBeginning = true;
            await _playbackBackend.SeekAsync(resumePosition, cancellationToken);
            await _playbackBackend.PlayAsync(cancellationToken);
            return new ShellWorkflowTransitionResult
            {
                StatusMessage = "Captions ready. Playing with generated subtitles."
            };
        }

        if (!snapshot.IsCaptionGenerationInProgress)
        {
            ResetCaptionStartupGate();
        }

        return new ShellWorkflowTransitionResult();
    }

    private async Task<ShellWorkflowTransitionResult> PauseForWorkflowTransitionAsync(string statusMessage, CancellationToken cancellationToken)
    {
        await _playbackBackend.PauseAsync(cancellationToken);
        await WaitForPauseStateAsync(true, cancellationToken);
        return new ShellWorkflowTransitionResult
        {
            StatusMessage = statusMessage
        };
    }

    private async Task WaitForPauseStateAsync(bool paused, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_playbackBackend.Clock.Current.IsPaused == paused)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private void ResetCaptionStartupGate()
    {
        _autoResumePlaybackAfterCaptionReady = false;
        _autoResumePlaybackPath = null;
        _autoResumePlaybackPosition = TimeSpan.Zero;
        _autoResumePlaybackFromBeginning = true;
    }
}
