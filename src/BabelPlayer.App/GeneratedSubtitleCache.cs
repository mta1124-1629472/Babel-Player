using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Thread-safe LRU cache for generated subtitle cues.
/// Evicts the least recently used entry when the capacity limit is reached,
/// preventing unbounded memory growth in long sessions.
/// </summary>
internal sealed class GeneratedSubtitleCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<(string Key, List<SubtitleCue> Cues)>> _index;
    private readonly LinkedList<(string Key, List<SubtitleCue> Cues)> _lruOrder = new();
    private readonly Lock _lock = new();

    public GeneratedSubtitleCache(int capacity = 20)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        _capacity = capacity;
        _index = new Dictionary<string, LinkedListNode<(string, List<SubtitleCue>)>>(capacity, StringComparer.OrdinalIgnoreCase);
    }

    public void Store(string videoPath, string transcriptionModelKey, IReadOnlyList<SubtitleCue> cues)
    {
        var key = GetKey(videoPath, transcriptionModelKey);
        var cloned = SubtitleCueSessionMapper.CloneCues(cues).ToList();

        lock (_lock)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                _lruOrder.Remove(existing);
                _index.Remove(key);
            }
            else if (_lruOrder.Count >= _capacity)
            {
                // Evict least recently used (tail of the list)
                var lru = _lruOrder.Last!;
                _index.Remove(lru.Value.Key);
                _lruOrder.RemoveLast();
            }

            var node = _lruOrder.AddFirst((key, cloned));
            _index[key] = node;
        }
    }

    public bool TryGet(string videoPath, string transcriptionModelKey, out IReadOnlyList<SubtitleCue> cues)
    {
        var key = GetKey(videoPath, transcriptionModelKey);

        lock (_lock)
        {
            if (!_index.TryGetValue(key, out var node))
            {
                cues = [];
                return false;
            }

            // Promote to front (most recently used)
            _lruOrder.Remove(node);
            _lruOrder.AddFirst(node);

            cues = SubtitleCueSessionMapper.CloneCues(node.Value.Cues);
            return true;
        }
    }

    public int Count
    {
        get { lock (_lock) return _lruOrder.Count; }
    }

    private static string GetKey(string videoPath, string transcriptionModelKey)
        => $"{Path.GetFullPath(videoPath)}|{transcriptionModelKey}";
}
