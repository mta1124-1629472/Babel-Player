namespace BabelPlayer.App;

public interface IMediaSessionStore
{
    event Action<MediaSessionSnapshot>? SnapshotChanged;

    MediaSessionSnapshot Snapshot { get; }
}

public sealed class InMemoryMediaSessionStore : IMediaSessionStore
{
    private readonly object _sync = new();
    private MediaSessionSnapshot _snapshot = new();

    public event Action<MediaSessionSnapshot>? SnapshotChanged;

    public MediaSessionSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return MediaSessionSnapshotCloner.Clone(_snapshot);
            }
        }
    }

    public void Update(Func<MediaSessionSnapshot, MediaSessionSnapshot> update)
    {
        MediaSessionSnapshot snapshot;
        lock (_sync)
        {
            _snapshot = MediaSessionSnapshotCloner.Clone(update(MediaSessionSnapshotCloner.Clone(_snapshot)));
            snapshot = MediaSessionSnapshotCloner.Clone(_snapshot);
        }

        SnapshotChanged?.Invoke(snapshot);
    }
}
