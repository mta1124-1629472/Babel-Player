using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class PlaybackSessionController
{
    private readonly PlaylistController _playlistController;

    public PlaybackSessionController(PlaylistController playlistController)
    {
        _playlistController = playlistController;
    }

    public PlaylistItem? CurrentItem => _playlistController.CurrentItem;

    public PlaylistItem? StartWith(PlaylistItem item)
    {
        var index = _playlistController.Items
            .Select((candidate, candidateIndex) => new { candidate, candidateIndex })
            .FirstOrDefault(entry => string.Equals(entry.candidate.Path, item.Path, StringComparison.OrdinalIgnoreCase))
            ?.candidateIndex ?? -1;
        return index >= 0 ? _playlistController.SelectIndex(index) : null;
    }

    public PlaylistItem? MoveNext() => _playlistController.MoveNext();

    public PlaylistItem? MovePrevious() => _playlistController.MovePrevious();

    public PlaybackResumeEntry? BuildResumeEntry(PlaybackStateSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Path) || snapshot.Duration <= TimeSpan.Zero || snapshot.Position <= TimeSpan.Zero)
        {
            return null;
        }

        return new PlaybackResumeEntry
        {
            Path = snapshot.Path,
            PositionSeconds = snapshot.Position.TotalSeconds,
            DurationSeconds = snapshot.Duration.TotalSeconds,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}
