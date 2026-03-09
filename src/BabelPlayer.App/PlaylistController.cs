using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class PlaylistController
{
    private readonly List<PlaylistItem> _items = [];

    public IReadOnlyList<PlaylistItem> Items => _items;
    public int CurrentIndex { get; private set; } = -1;
    public PlaylistItem? CurrentItem => CurrentIndex >= 0 && CurrentIndex < _items.Count ? _items[CurrentIndex] : null;

    public IReadOnlyList<PlaylistItem> EnqueueFiles(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        List<PlaylistItem> added = [];
        foreach (var file in files.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var item = new PlaylistItem
            {
                Path = file,
                DisplayName = System.IO.Path.GetFileName(file)
            };

            _items.Add(item);
            added.Add(item);
        }

        if (CurrentIndex < 0 && _items.Count > 0)
        {
            CurrentIndex = 0;
        }

        return added;
    }

    public IReadOnlyList<PlaylistItem> EnqueueFolder(string folderPath, IEnumerable<string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentNullException.ThrowIfNull(files);

        List<PlaylistItem> added = [];
        foreach (var file in files.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var item = new PlaylistItem
            {
                Path = file,
                DisplayName = System.IO.Path.GetFileName(file),
                IsDirectorySeed = true
            };

            _items.Add(item);
            added.Add(item);
        }

        if (CurrentIndex < 0 && _items.Count > 0)
        {
            CurrentIndex = 0;
        }

        return added;
    }

    public PlaylistItem? SelectIndex(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return null;
        }

        CurrentIndex = index;
        return _items[index];
    }

    public PlaylistItem? MoveNext(bool wrap = false)
    {
        if (_items.Count == 0)
        {
            CurrentIndex = -1;
            return null;
        }

        if (CurrentIndex + 1 < _items.Count)
        {
            CurrentIndex++;
            return _items[CurrentIndex];
        }

        if (wrap)
        {
            CurrentIndex = 0;
            return _items[CurrentIndex];
        }

        return null;
    }

    public PlaylistItem? MovePrevious(bool wrap = false)
    {
        if (_items.Count == 0)
        {
            CurrentIndex = -1;
            return null;
        }

        if (CurrentIndex > 0)
        {
            CurrentIndex--;
            return _items[CurrentIndex];
        }

        if (wrap)
        {
            CurrentIndex = _items.Count - 1;
            return _items[CurrentIndex];
        }

        return null;
    }

    public PlaylistItem? AdvanceAfterMediaEnded() => MoveNext();

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return;
        }

        _items.RemoveAt(index);
        if (_items.Count == 0)
        {
            CurrentIndex = -1;
            return;
        }

        if (index < CurrentIndex)
        {
            CurrentIndex--;
            return;
        }

        if (CurrentIndex >= _items.Count)
        {
            CurrentIndex = _items.Count - 1;
        }
    }

    public void Clear()
    {
        _items.Clear();
        CurrentIndex = -1;
    }
}
