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

public sealed class ShellController
{
    private readonly PlaylistController _playlistController;
    private readonly PlaybackSessionController _playbackSessionController;
    private readonly IPlaybackBackend _playbackBackend;
    private readonly SubtitleWorkflowController _subtitleWorkflowController;
    private readonly LibraryBrowserService _libraryBrowserService;
    private readonly ResumePlaybackService _resumePlaybackService;

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

    public async Task<bool> LoadPlaylistItemAsync(
        PlaylistItem? item,
        ShellLoadMediaOptions options,
        CancellationToken cancellationToken)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
        {
            return false;
        }

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

    public ShellPlaybackOpenResult HandleMediaOpened(string? path, TimeSpan duration, bool resumeEnabled)
    {
        var current = _playlistController.CurrentItem;
        if (!resumeEnabled)
        {
            return new ShellPlaybackOpenResult
            {
                StatusMessage = current is null ? "Media opened." : $"Now playing {current.DisplayName}."
            };
        }

        var entry = _resumePlaybackService.FindEntry(path, duration);
        return new ShellPlaybackOpenResult
        {
            ResumePosition = entry is null ? null : TimeSpan.FromSeconds(Math.Clamp(entry.PositionSeconds, 0, duration.TotalSeconds)),
            StatusMessage = current is null ? "Media opened." : $"Now playing {current.DisplayName}."
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

        var next = _playlistController.AdvanceAfterMediaEnded();
        return new ShellMediaEndedResult
        {
            NextItem = next,
            StatusMessage = next is null ? "Playback ended." : $"Now playing {next.DisplayName}."
        };
    }
}
