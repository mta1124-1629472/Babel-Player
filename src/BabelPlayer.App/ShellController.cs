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
    public bool IsMuted { get; init; }
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

public sealed record ShellSubtitleTrackSelectionResult
{
    public int? SelectedSubtitleTrackId { get; init; }
    public bool TrackSelectionChanged { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
    public bool IsError { get; init; }
}

public sealed record ShellInitializationSnapshot
{
    public AppPlayerSettings Settings { get; init; } = new();
    public double AudioDelaySeconds { get; init; }
    public double SubtitleDelaySeconds { get; init; }
    public string SelectedAspectRatio { get; init; } = "auto";
    public double Volume { get; init; } = 0.8;
    public bool IsMuted { get; init; }
    public double PlaybackRate { get; init; } = 1.0;
    public SubtitleRenderMode LastNonOffSubtitleRenderMode { get; init; } = SubtitleRenderMode.TranslationOnly;
    public bool ShowSubtitleSource { get; init; }
    public string DefaultTranscriptionLabel { get; init; } = string.Empty;
    public string DefaultTranslationLabel { get; init; } = string.Empty;
    public bool ResumeEnabled { get; init; }
    public IReadOnlyList<LibraryNode> PinnedRoots { get; init; } = [];
}

public sealed class ShellController : IDisposable
{
    private readonly PlaybackQueueController _playbackQueueController;
    private readonly IPlaybackBackend _playbackBackend;
    private readonly SubtitleWorkflowController _subtitleWorkflowController;
    private readonly LibraryBrowserService _libraryBrowserService;
    private readonly ResumePlaybackService _resumePlaybackService;
    private readonly SettingsFacade _settingsFacade;
    private readonly ResumeTrackingCoordinator _resumeTrackingCoordinator;
    private readonly IBabelLogger _logger;

    private bool _autoResumePlaybackAfterCaptionReady;
    private string? _autoResumePlaybackPath;
    private TimeSpan _autoResumePlaybackPosition = TimeSpan.Zero;
    private bool _autoResumePlaybackFromBeginning = true;

    public event Action<PlaybackQueueSnapshot>? QueueSnapshotChanged;

    public ShellController(
        PlaybackQueueController playbackQueueController,
        IPlaybackBackend playbackBackend,
        SubtitleWorkflowController subtitleWorkflowController,
        LibraryBrowserService libraryBrowserService,
        ResumePlaybackService resumePlaybackService,
        SettingsFacade? settingsFacade = null,
        IBabelLogFactory? logFactory = null)
    {
        _playbackQueueController = playbackQueueController;
        _playbackBackend = playbackBackend;
        _subtitleWorkflowController = subtitleWorkflowController;
        _libraryBrowserService = libraryBrowserService;
        _resumePlaybackService = resumePlaybackService;
        _settingsFacade = settingsFacade ?? new SettingsFacade();
        _logger = (logFactory ?? NullBabelLogFactory.Instance).CreateLogger("shell.controller");
        _resumeTrackingCoordinator = new ResumeTrackingCoordinator(playbackBackend, resumePlaybackService);
        _playbackQueueController.SnapshotChanged += HandleQueueSnapshotChanged;
    }

    public PlaybackQueueSnapshot QueueSnapshot => _playbackQueueController.Snapshot;

    public PlaylistItem? NowPlayingItem => _playbackQueueController.NowPlayingItem;

    public ShellInitializationSnapshot BuildInitializationSnapshot()
    {
        var settings = _settingsFacade.Load();
        if (settings.PinnedRoots.Count == 0)
        {
            var defaultVideosPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var pinnedRoots = Directory.Exists(defaultVideosPath) ? new List<string> { defaultVideosPath } : [];
            settings = settings with
            {
                PinnedRoots = pinnedRoots,
                ShowBrowserPanel = pinnedRoots.Count > 0,
                ShowPlaylistPanel = true
            };
        }

        settings = settings with { WindowMode = PlaybackWindowMode.Standard };

        var volume = Math.Clamp(settings.VolumeLevel, 0, 1);
        var aspectRatio = string.IsNullOrWhiteSpace(settings.AspectRatioOverride) ? "auto" : settings.AspectRatioOverride;
        var lastNonOffMode = settings.SubtitleRenderMode == SubtitleRenderMode.Off
            ? SubtitleRenderMode.TranslationOnly
            : settings.SubtitleRenderMode;
        var showSource = settings.SubtitleRenderMode is SubtitleRenderMode.SourceOnly or SubtitleRenderMode.Dual;

        _resumeTrackingCoordinator.SetEnabled(settings.ResumeEnabled);

        var pinnedRootNodes = _libraryBrowserService.BuildPinnedRoots(settings.PinnedRoots);

        _logger.LogInfo("Built shell initialization snapshot.", BabelLogContext.Create(
            ("volume", volume), ("muted", settings.IsMuted), ("resumeEnabled", settings.ResumeEnabled),
            ("pinnedRoots", settings.PinnedRoots.Count)));

        return new ShellInitializationSnapshot
        {
            Settings = settings,
            AudioDelaySeconds = settings.AudioDelaySeconds,
            SubtitleDelaySeconds = settings.SubtitleDelaySeconds,
            SelectedAspectRatio = aspectRatio,
            Volume = volume,
            IsMuted = settings.IsMuted,
            PlaybackRate = settings.DefaultPlaybackRate,
            LastNonOffSubtitleRenderMode = lastNonOffMode,
            ShowSubtitleSource = showSource,
            DefaultTranscriptionLabel = SubtitleWorkflowCatalog.GetTranscriptionModel("local:tiny-multilingual").DisplayName,
            DefaultTranslationLabel = SubtitleWorkflowCatalog.GetTranslationModel(null).DisplayName,
            ResumeEnabled = settings.ResumeEnabled,
            PinnedRoots = pinnedRootNodes
        };
    }

    public IReadOnlyList<PlaylistItem> QueueItems => _playbackQueueController.QueueItems;

    public IReadOnlyList<PlaylistItem> HistoryItems => _playbackQueueController.HistoryItems;

    public PlaybackStateSnapshot CurrentPlaybackSnapshot => _resumeTrackingCoordinator.CurrentSnapshot with
    {
        PlaylistIndex = _playbackQueueController.NowPlayingItem is null ? -1 : 0,
        PlaylistCount = _playbackQueueController.QueueItems.Count
    };

    public ShellQueueMediaResult EnqueueFiles(IEnumerable<string> files, bool autoplay)
    {
        var entries = files
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        if (entries.Count == 0)
        {
            return new ShellQueueMediaResult
            {
                StatusMessage = "No supported media files were selected.",
                IsError = true
            };
        }

        if (!autoplay)
        {
            var added = _playbackQueueController.AddToQueue(entries);
            _logger.LogInfo("Queued media files.", BabelLogContext.Create(("count", added.Count)));
            return new ShellQueueMediaResult
            {
                AddedItems = added,
                StatusMessage = $"Queued {added.Count} item(s)."
            };
        }

        var itemToLoad = _playbackQueueController.PlayNow(entries[0]);
        var addedItems = entries.Count > 1
            ? _playbackQueueController.AddToQueue(entries.Skip(1))
            : [];
        _logger.LogInfo("Play now requested from file list.", BabelLogContext.Create(("path", itemToLoad.Path), ("addedToQueue", addedItems.Count)));
        return new ShellQueueMediaResult
        {
            AddedItems = addedItems,
            ItemToLoad = itemToLoad,
            StatusMessage = addedItems.Count > 0
                ? $"Now playing {itemToLoad.DisplayName}. Added {addedItems.Count} item(s) to Up Next."
                : $"Now playing {itemToLoad.DisplayName}."
        };
    }

    public ShellQueueMediaResult EnqueueFolder(string folderPath, bool autoplay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var files = _libraryBrowserService.EnumerateMediaFiles(folderPath, recursive: true)
            .ToList();
        if (files.Count == 0)
        {
            return new ShellQueueMediaResult
            {
                StatusMessage = $"No new supported media files were found in {folderPath}.",
                IsError = true
            };
        }

        PlaylistItem? itemToLoad = null;
        IReadOnlyList<PlaylistItem> added;
        if (autoplay)
        {
            itemToLoad = _playbackQueueController.PlayNow(new PlaylistItem
            {
                Path = files[0],
                DisplayName = Path.GetFileName(files[0]),
                IsDirectorySeed = true
            });
            added = files.Count > 1
                ? _playbackQueueController.AddFolderToQueue(folderPath, files.Skip(1))
                : [];
        }
        else
        {
            added = _playbackQueueController.AddFolderToQueue(folderPath, files);
        }
        _logger.LogInfo("Queued folder.", BabelLogContext.Create(("folderPath", folderPath), ("autoplay", autoplay), ("addedCount", added.Count), ("playNowPath", itemToLoad?.Path)));

        return new ShellQueueMediaResult
        {
            AddedItems = added,
            ItemToLoad = itemToLoad,
            PinnedFolders = [folderPath],
            StatusMessage = autoplay
                ? itemToLoad is null
                    ? $"Queued {added.Count} item(s) from {Path.GetFileName(folderPath)}."
                    : added.Count > 0
                        ? $"Now playing {itemToLoad.DisplayName}. Added {added.Count} item(s) from {Path.GetFileName(folderPath)} to Up Next."
                        : $"Now playing {itemToLoad.DisplayName}."
                : $"Queued {added.Count} item(s) from {Path.GetFileName(folderPath)}."
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

        var itemToLoad = _playbackQueueController.PlayNow(discoveredFiles[0]);
        var added = discoveredFiles.Count > 1
            ? _playbackQueueController.AddToQueue(discoveredFiles.Skip(1))
            : [];
        _logger.LogInfo("Queued dropped items.", BabelLogContext.Create(("fileCount", discoveredFiles.Count), ("folderCount", pinnedFolders.Count), ("playNowPath", itemToLoad.Path)));
        return new ShellQueueMediaResult
        {
            AddedItems = added,
            ItemToLoad = itemToLoad,
            PinnedFolders = pinnedFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    public ShellQueueMediaResult PlayNow(string path)
    {
        var item = _playbackQueueController.PlayNow(path);
        _logger.LogInfo("Play now requested.", BabelLogContext.Create(("path", path)));
        return new ShellQueueMediaResult
        {
            ItemToLoad = item,
            StatusMessage = $"Now playing {item.DisplayName}."
        };
    }

    public ShellQueueMediaResult PlayNext(string path)
    {
        var added = _playbackQueueController.PlayNext([path]);
        _logger.LogInfo("Play next requested.", BabelLogContext.Create(("path", path), ("added", added.Count)));
        return new ShellQueueMediaResult
        {
            AddedItems = added,
            StatusMessage = added.Count == 0
                ? "Nothing was added to Up Next."
                : $"{added[0].DisplayName} will play next."
        };
    }

    public ShellQueueMediaResult AddToQueue(IEnumerable<string> files)
    {
        var added = _playbackQueueController.AddToQueue(files);
        _logger.LogInfo("Add to queue requested.", BabelLogContext.Create(("added", added.Count)));
        return new ShellQueueMediaResult
        {
            AddedItems = added,
            StatusMessage = added.Count == 0
                ? "Nothing was added to Up Next."
                : $"Queued {added.Count} item(s) in Up Next."
        };
    }

    public PlaylistItem? MovePrevious() => _playbackQueueController.MovePrevious();

    public PlaylistItem? MoveNext() => _playbackQueueController.MoveNext();

    public void RemoveQueueItemAt(int index)
    {
        _playbackQueueController.RemoveQueueItemAt(index);
    }

    public void ClearQueue()
    {
        _playbackQueueController.ClearQueue();
        _logger.LogInfo("Queue cleared.");
    }

    public async Task<bool> LoadPlaybackItemAsync(
        PlaylistItem? item,
        ShellLoadMediaOptions options,
        CancellationToken cancellationToken)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
        {
            _logger.LogWarning("Load playback item skipped because the item was missing.", null, BabelLogContext.Create(("path", item?.Path)));
            return false;
        }

        var operationId = $"media-{Guid.NewGuid():N}";
        _logger.LogInfo("Media load starting.", BabelLogContext.Create(("operationId", operationId), ("path", item.Path), ("displayName", item.DisplayName)));

        ResetCaptionStartupGate();

        _resumeTrackingCoordinator.SetEnabled(options.ResumeEnabled);
        if (options.ResumeEnabled)
        {
            _resumeTrackingCoordinator.Flush();
        }

        _resumeTrackingCoordinator.ResetForMedia(item.Path);

        try
        {
            await _playbackBackend.LoadAsync(item.Path, cancellationToken);
            await _playbackBackend.SetHardwareDecodingModeAsync(options.HardwareDecodingMode, cancellationToken);
            await _playbackBackend.SetPlaybackRateAsync(options.PlaybackRate, cancellationToken);
            await _playbackBackend.SetAspectRatioAsync(options.AspectRatio, cancellationToken);
            await _playbackBackend.SetAudioDelayAsync(options.AudioDelaySeconds, cancellationToken);
            await _playbackBackend.SetSubtitleDelayAsync(options.SubtitleDelaySeconds, cancellationToken);
            await _playbackBackend.SetZoomAsync(0, cancellationToken);
            await _playbackBackend.SetPanAsync(0, 0, cancellationToken);
            await _playbackBackend.SetVolumeAsync(options.Volume, cancellationToken);
            await _playbackBackend.SetMuteAsync(options.IsMuted, cancellationToken);
            await _subtitleWorkflowController.LoadMediaSubtitlesAsync(item.Path, cancellationToken);
            _logger.LogInfo("Media load completed.", BabelLogContext.Create(("operationId", operationId), ("path", item.Path)));
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Media load failed.", ex, BabelLogContext.Create(("operationId", operationId), ("path", item.Path), ("displayName", item.DisplayName)));
            throw;
        }
    }

    public async Task<ShellPlaybackOpenResult> HandleMediaOpenedAsync(PlaybackStateSnapshot snapshot, bool resumeEnabled, CancellationToken cancellationToken = default)
    {
        var current = _playbackQueueController.NowPlayingItem;
        var result = new ShellPlaybackOpenResult
        {
            StatusMessage = current is null ? "Media opened." : $"Now playing {current.DisplayName}."
        };

        _resumeTrackingCoordinator.SetEnabled(resumeEnabled);
        _resumeTrackingCoordinator.ResetForMedia(snapshot.Path);
        if (!resumeEnabled)
        {
            return result;
        }

        var entry = _resumePlaybackService.FindEntry(snapshot.Path, snapshot.Duration);
        if (entry is null)
        {
            return result;
        }

        var resumePosition = TimeSpan.FromSeconds(Math.Clamp(entry.PositionSeconds, 0, snapshot.Duration.TotalSeconds));
        await _playbackBackend.SeekAsync(resumePosition, cancellationToken);
        _logger.LogInfo("Resume position applied.", BabelLogContext.Create(("path", snapshot.Path), ("resumePosition", resumePosition)));
        return result with
        {
            ResumePosition = resumePosition
        };
    }

    public void SetResumeTrackingEnabled(bool enabled)
    {
        _resumeTrackingCoordinator.SetEnabled(enabled);
    }

    public void ClearResumeHistory()
    {
        _resumePlaybackService.Clear();
    }

    public void FlushResumeTracking(bool forceRemoveCompleted = false)
    {
        _resumeTrackingCoordinator.Flush(forceRemoveCompleted);
    }

    public ShellMediaEndedResult HandleMediaEnded(bool resumeEnabled)
    {
        _resumeTrackingCoordinator.SetEnabled(resumeEnabled);
        if (resumeEnabled)
        {
            _resumeTrackingCoordinator.Flush(forceRemoveCompleted: true);
        }

        ResetCaptionStartupGate();
        var next = _playbackQueueController.AdvanceAfterMediaEnded();
        _logger.LogInfo("Handled media end.", BabelLogContext.Create(("nextPath", next?.Path), ("resumeEnabled", resumeEnabled)));
        return new ShellMediaEndedResult
        {
            NextItem = next,
            StatusMessage = next is null ? "Playback ended." : $"Now playing {next.DisplayName}."
        };
    }

    public void Dispose()
    {
        _playbackQueueController.SnapshotChanged -= HandleQueueSnapshotChanged;
        _resumeTrackingCoordinator.Dispose();
    }

    private void HandleQueueSnapshotChanged(PlaybackQueueSnapshot snapshot)
    {
        _logger.LogInfo("Queue snapshot changed.", BabelLogContext.Create(("nowPlaying", snapshot.NowPlayingItem?.DisplayName), ("upNextCount", snapshot.QueueItems.Count), ("historyCount", snapshot.HistoryItems.Count)));
        QueueSnapshotChanged?.Invoke(snapshot);
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

    public async Task ApplyAudioPreferencesAsync(double volume, bool muted, CancellationToken cancellationToken = default)
    {
        await _playbackBackend.SetVolumeAsync(volume, cancellationToken);
        await _playbackBackend.SetMuteAsync(muted, cancellationToken);
    }

    public Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken = default)
        => _playbackBackend.SetPlaybackRateAsync(speed, cancellationToken);

    public Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken = default)
        => _playbackBackend.SetAudioTrackAsync(trackId, cancellationToken);

    public Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken = default)
        => _playbackBackend.SetSubtitleTrackAsync(trackId, cancellationToken);

    public async Task<ShellSubtitleTrackSelectionResult> SelectEmbeddedSubtitleTrackAsync(
        string? currentPath,
        SubtitlePipelineSource currentSubtitleSource,
        MediaTrackInfo? track,
        CancellationToken cancellationToken = default)
    {
        if (track is null)
        {
            await _playbackBackend.SetSubtitleTrackAsync(null, cancellationToken);
            if (currentSubtitleSource == SubtitlePipelineSource.EmbeddedTrack && !string.IsNullOrWhiteSpace(currentPath))
            {
                await _subtitleWorkflowController.LoadMediaSubtitlesAsync(currentPath, cancellationToken);
            }

            _logger.LogInfo("Embedded subtitle track disabled.", BabelLogContext.Create(("path", currentPath), ("subtitleSource", currentSubtitleSource.ToString())));
            return new ShellSubtitleTrackSelectionResult
            {
                TrackSelectionChanged = true,
                StatusMessage = "Embedded subtitle track disabled."
            };
        }

        if (track.IsTextBased)
        {
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                return new ShellSubtitleTrackSelectionResult
                {
                    StatusMessage = "Open a video first.",
                    IsError = true
                };
            }

            await _playbackBackend.SetSubtitleTrackAsync(null, cancellationToken);
            var loadResult = await _subtitleWorkflowController.ImportEmbeddedSubtitleTrackAsync(currentPath, track, cancellationToken);
            var imported = loadResult.CueCount > 0;
            _logger.LogInfo(
                "Embedded text subtitle track imported.",
                BabelLogContext.Create(("path", currentPath), ("trackId", track.Id), ("cueCount", loadResult.CueCount)));
            return new ShellSubtitleTrackSelectionResult
            {
                TrackSelectionChanged = true,
                StatusMessage = imported
                    ? $"Imported embedded subtitle track {track.Id}."
                    : "Embedded subtitle import failed.",
                IsError = !imported
            };
        }

        await _playbackBackend.SetSubtitleTrackAsync(track.Id, cancellationToken);
        _logger.LogInfo(
            "Embedded image subtitle track selected for direct playback.",
            BabelLogContext.Create(("path", currentPath), ("trackId", track.Id)));
        return new ShellSubtitleTrackSelectionResult
        {
            SelectedSubtitleTrackId = track.Id,
            TrackSelectionChanged = true,
            StatusMessage = "Selected image-based embedded subtitle track for direct playback."
        };
    }

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
