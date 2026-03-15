using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Queue-mutation commands: enqueue files/folders/drops, play-now/next,
/// reorder, remove, clear.
/// </summary>
public sealed partial class ShellController
{
    public ShellQueueMediaResult EnqueueFiles(IEnumerable<string> files, bool autoplay)
    {
        var entries = files.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (entries.Count == 0)
            return Error("No supported media files were selected.");

        if (!autoplay)
        {
            var added = _playbackQueueController.AddToQueue(entries);
            _logger.LogInfo("Queued media files.", BabelLogContext.Create(("count", added.Count)));
            return new ShellQueueMediaResult
            {
                AddedItems    = added.Select(i => i.ToShell()).ToArray(),
                StatusMessage = $"Queued {added.Count} item(s)."
            };
        }

        var item   = _playbackQueueController.PlayNow(entries[0]);
        var queued = entries.Count > 1 ? _playbackQueueController.AddToQueue(entries.Skip(1)) : [];
        _logger.LogInfo("Play now from file list.",
            BabelLogContext.Create(("path", item.Path), ("addedToQueue", queued.Count)));
        return new ShellQueueMediaResult
        {
            AddedItems    = queued.Select(i => i.ToShell()).ToArray(),
            ItemToLoad    = item.ToShell(),
            StatusMessage = queued.Count > 0
                ? $"Now playing {item.DisplayName}. Added {queued.Count} item(s) to Up Next."
                : $"Now playing {item.DisplayName}."
        };
    }

    public ShellQueueMediaResult EnqueueFolder(string folderPath, bool autoplay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var files = _libraryBrowserService.EnumerateMediaFiles(folderPath, recursive: true).ToList();
        if (files.Count == 0)
            return Error($"No new supported media files were found in {folderPath}.");

        Core.PlaylistItem? itemToLoad = null;
        IReadOnlyList<Core.PlaylistItem> added;

        if (autoplay)
        {
            itemToLoad = _playbackQueueController.PlayNow(new Core.PlaylistItem
            {
                Path            = files[0],
                DisplayName     = System.IO.Path.GetFileName(files[0]),
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

        _logger.LogInfo("Queued folder.",
            BabelLogContext.Create(
                ("folderPath",  folderPath),
                ("autoplay",    autoplay),
                ("addedCount",  added.Count),
                ("playNowPath", itemToLoad?.Path)));

        return new ShellQueueMediaResult
        {
            AddedItems         = added.Select(i => i.ToShell()).ToArray(),
            ItemToLoad         = itemToLoad?.ToShell(),
            PinnedFolders      = [folderPath],
            RevealBrowserPane  = true,
            UpdatedPreferences = RevealBrowserPanePreference(),
            StatusMessage      = autoplay
                ? itemToLoad is null
                    ? $"Queued {added.Count} item(s) from {System.IO.Path.GetFileName(folderPath)}."
                    : added.Count > 0
                        ? $"Now playing {itemToLoad.DisplayName}. Added {added.Count} item(s) from {System.IO.Path.GetFileName(folderPath)} to Up Next."
                        : $"Now playing {itemToLoad.DisplayName}."
                : $"Queued {added.Count} item(s) from {System.IO.Path.GetFileName(folderPath)}."
        };
    }

    public ShellQueueMediaResult EnqueueDroppedItems(IEnumerable<string> files, IEnumerable<string> folders)
    {
        var discovered = new List<string>();
        var pinned     = new List<string>();

        foreach (var f in files.Where(LibraryBrowserService.IsSupportedMediaFile))
            discovered.Add(f);

        foreach (var folder in folders.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            pinned.Add(folder);
            discovered.AddRange(_libraryBrowserService.EnumerateMediaFiles(folder, recursive: true));
        }

        if (discovered.Count == 0)
            return Error("Dropped items did not contain supported media files.");

        var item   = _playbackQueueController.PlayNow(discovered[0]);
        var queued = discovered.Count > 1 ? _playbackQueueController.AddToQueue(discovered.Skip(1)) : [];

        _logger.LogInfo("Queued dropped items.",
            BabelLogContext.Create(
                ("fileCount",   discovered.Count),
                ("folderCount", pinned.Count),
                ("playNowPath", item.Path)));

        var distinctPinned = pinned.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new ShellQueueMediaResult
        {
            AddedItems        = queued.Select(i => i.ToShell()).ToArray(),
            ItemToLoad        = item.ToShell(),
            PinnedFolders     = distinctPinned,
            RevealBrowserPane = distinctPinned.Length > 0,
            UpdatedPreferences= distinctPinned.Length > 0 ? RevealBrowserPanePreference() : null
        };
    }

    public ShellQueueMediaResult AddDroppedItemsToQueue(IEnumerable<string> files, IEnumerable<string> folders)
    {
        var discovered = new List<string>();
        foreach (var f in files.Where(LibraryBrowserService.IsSupportedMediaFile))
            discovered.Add(f);
        foreach (var folder in folders.Where(p => !string.IsNullOrWhiteSpace(p)))
            discovered.AddRange(_libraryBrowserService.EnumerateMediaFiles(folder, recursive: true));

        var added = _playbackQueueController.AddToQueue(
            discovered.Distinct(StringComparer.OrdinalIgnoreCase));

        _logger.LogInfo("Added dropped items to queue.", BabelLogContext.Create(("added", added.Count)));
        return new ShellQueueMediaResult
        {
            AddedItems    = added.Select(i => i.ToShell()).ToArray(),
            StatusMessage = added.Count == 0
                ? "Dropped items did not contain supported media files."
                : $"Queued {added.Count} item(s) in Up Next.",
            IsError = added.Count == 0
        };
    }

    public ShellQueueMediaResult PlayNow(string path)
    {
        var item = _playbackQueueController.PlayNow(path);
        _logger.LogInfo("Play now.", BabelLogContext.Create(("path", path)));
        return new ShellQueueMediaResult
        {
            ItemToLoad    = item.ToShell(),
            StatusMessage = $"Now playing {item.DisplayName}."
        };
    }

    public ShellQueueMediaResult PlayNext(string path)
    {
        var added = _playbackQueueController.PlayNext([path]);
        _logger.LogInfo("Play next.", BabelLogContext.Create(("path", path), ("added", added.Count)));
        return new ShellQueueMediaResult
        {
            AddedItems    = added.Select(i => i.ToShell()).ToArray(),
            StatusMessage = added.Count == 0
                ? "Nothing was added to Up Next."
                : $"{added[0].DisplayName} will play next."
        };
    }

    public ShellQueueMediaResult AddToQueue(IEnumerable<string> files)
    {
        var added = _playbackQueueController.AddToQueue(files);
        _logger.LogInfo("Add to queue.", BabelLogContext.Create(("added", added.Count)));
        return new ShellQueueMediaResult
        {
            AddedItems    = added.Select(i => i.ToShell()).ToArray(),
            StatusMessage = added.Count == 0
                ? "Nothing was added to Up Next."
                : $"Queued {added.Count} item(s) in Up Next."
        };
    }

    public ShellPlaylistItem? MovePrevious() => _playbackQueueController.MovePrevious()?.ToShell();
    public ShellPlaylistItem? MoveNext()     => _playbackQueueController.MoveNext()?.ToShell();

    public ShellQueueMediaResult ReorderQueueItem(int sourceIndex, int? targetIndex)
    {
        if (!_playbackQueueController.ReorderQueueItem(sourceIndex, targetIndex))
            return Error("Unable to reorder queue item.");

        _logger.LogInfo("Reordered queue item.",
            BabelLogContext.Create(("sourceIndex", sourceIndex), ("targetIndex", targetIndex)));
        return new ShellQueueMediaResult { StatusMessage = "Queue order updated." };
    }

    public void RemoveQueueItemAt(int index) => _playbackQueueController.RemoveQueueItemAt(index);

    public void ClearQueue()
    {
        _playbackQueueController.ClearQueue();
        _logger.LogInfo("Queue cleared.");
    }

    private static ShellQueueMediaResult Error(string message) =>
        new() { IsError = true, StatusMessage = message };
}
