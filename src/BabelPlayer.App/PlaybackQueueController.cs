using BabelPlayer.Core;
using System.IO;

namespace BabelPlayer.App;

public sealed record PlaybackQueueSnapshot
{
    public PlaylistItem? NowPlayingItem { get; init; }
    public IReadOnlyList<PlaylistItem> QueueItems { get; init; } = [];
    public IReadOnlyList<PlaylistItem> HistoryItems { get; init; } = [];
}

public sealed class PlaybackQueueController
{
    private readonly List<PlaylistItem> _queueItems = [];
    private readonly List<PlaylistItem> _historyItems = [];

    public event Action<PlaybackQueueSnapshot>? SnapshotChanged;

    public PlaylistItem? NowPlayingItem { get; private set; }

    public IReadOnlyList<PlaylistItem> QueueItems => _queueItems;

    public IReadOnlyList<PlaylistItem> HistoryItems => _historyItems;

    public PlaybackQueueSnapshot Snapshot => new()
    {
        NowPlayingItem = NowPlayingItem,
        QueueItems = _queueItems.ToArray(),
        HistoryItems = _historyItems.ToArray()
    };

    public PlaylistItem CreateItem(string path, bool isDirectorySeed = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new PlaylistItem
        {
            Path = path,
            DisplayName = Path.GetFileName(path),
            IsDirectorySeed = isDirectorySeed
        };
    }

    public IReadOnlyList<PlaylistItem> AddToQueue(IEnumerable<string> files)
    {
        return InsertIntoQueue(_queueItems.Count, files, isDirectorySeed: false);
    }

    public IReadOnlyList<PlaylistItem> PlayNext(IEnumerable<string> files)
    {
        return InsertIntoQueue(0, files, isDirectorySeed: false);
    }

    public IReadOnlyList<PlaylistItem> AddFolderToQueue(string folderPath, IEnumerable<string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        return InsertIntoQueue(_queueItems.Count, files, isDirectorySeed: true);
    }

    public IReadOnlyList<PlaylistItem> PlayFolderNext(string folderPath, IEnumerable<string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        return InsertIntoQueue(0, files, isDirectorySeed: true);
    }

    public PlaylistItem PlayNow(string path)
    {
        return PlayNow(CreateItem(path));
    }

    public PlaylistItem PlayNow(PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (NowPlayingItem is not null
            && string.Equals(NowPlayingItem.Path, item.Path, StringComparison.OrdinalIgnoreCase))
        {
            NowPlayingItem = item;
            RaiseSnapshotChanged();
            return item;
        }

        RemoveFirstMatch(_queueItems, item.Path);
        RemoveFirstMatch(_historyItems, item.Path);
        PushCurrentToHistory();
        NowPlayingItem = item;
        RaiseSnapshotChanged();
        return item;
    }

    public PlaylistItem? MoveNext()
    {
        if (_queueItems.Count == 0)
        {
            return null;
        }

        var next = _queueItems[0];
        _queueItems.RemoveAt(0);
        PushCurrentToHistory();
        NowPlayingItem = next;
        RaiseSnapshotChanged();
        return next;
    }

    public PlaylistItem? MovePrevious()
    {
        if (_historyItems.Count == 0)
        {
            return null;
        }

        var previous = _historyItems[0];
        _historyItems.RemoveAt(0);
        if (NowPlayingItem is not null)
        {
            _queueItems.Insert(0, NowPlayingItem);
        }

        NowPlayingItem = previous;
        RaiseSnapshotChanged();
        return previous;
    }

    public PlaylistItem? AdvanceAfterMediaEnded()
    {
        if (_queueItems.Count > 0)
        {
            return MoveNext();
        }

        if (NowPlayingItem is null)
        {
            return null;
        }

        PushCurrentToHistory();
        NowPlayingItem = null;
        RaiseSnapshotChanged();
        return null;
    }

    public void RemoveQueueItemAt(int index)
    {
        if (index < 0 || index >= _queueItems.Count)
        {
            return;
        }

        _queueItems.RemoveAt(index);
        RaiseSnapshotChanged();
    }

    public bool ReorderQueueItem(int sourceIndex, int? targetIndex)
    {
        if (sourceIndex < 0 || sourceIndex >= _queueItems.Count)
        {
            return false;
        }

        var item = _queueItems[sourceIndex];
        _queueItems.RemoveAt(sourceIndex);

        int insertIndex;
        if (targetIndex is null)
        {
            insertIndex = _queueItems.Count;
        }
        else
        {
            var requestedTarget = targetIndex.Value;
            insertIndex = Math.Clamp(requestedTarget, 0, _queueItems.Count);
            if (sourceIndex < requestedTarget)
            {
                insertIndex = Math.Max(0, insertIndex - 1);
            }
        }

        _queueItems.Insert(insertIndex, item);
        RaiseSnapshotChanged();
        return true;
    }

    public void ClearQueue()
    {
        if (_queueItems.Count == 0)
        {
            return;
        }

        _queueItems.Clear();
        RaiseSnapshotChanged();
    }

    private IReadOnlyList<PlaylistItem> InsertIntoQueue(int insertIndex, IEnumerable<string> files, bool isDirectorySeed)
    {
        ArgumentNullException.ThrowIfNull(files);

        var newItems = files
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => CreateItem(path, isDirectorySeed))
            .ToList();
        if (newItems.Count == 0)
        {
            return [];
        }

        insertIndex = Math.Clamp(insertIndex, 0, _queueItems.Count);
        _queueItems.InsertRange(insertIndex, newItems);
        RaiseSnapshotChanged();
        return newItems;
    }

    private void PushCurrentToHistory()
    {
        if (NowPlayingItem is null)
        {
            return;
        }

        RemoveFirstMatch(_historyItems, NowPlayingItem.Path);
        _historyItems.Insert(0, NowPlayingItem);
    }

    private static void RemoveFirstMatch(List<PlaylistItem> items, string path)
    {
        var index = items.FindIndex(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            items.RemoveAt(index);
        }
    }

    private void RaiseSnapshotChanged()
    {
        SnapshotChanged?.Invoke(Snapshot);
    }
}
