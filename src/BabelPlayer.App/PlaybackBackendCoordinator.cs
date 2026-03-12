namespace BabelPlayer.App;

public sealed class PlaybackBackendCoordinator : IDisposable
{
    private readonly IPlaybackBackend _playbackBackend;
    private readonly MediaSessionCoordinator _mediaSessionCoordinator;
    private bool _disposed;

    public PlaybackBackendCoordinator(IPlaybackBackend playbackBackend, MediaSessionCoordinator mediaSessionCoordinator)
    {
        _playbackBackend = playbackBackend;
        _mediaSessionCoordinator = mediaSessionCoordinator;

        _playbackBackend.StateChanged += HandlePlaybackStateChanged;
        _playbackBackend.TracksChanged += HandleTracksChanged;
        _playbackBackend.Clock.Changed += HandleClockChanged;

        HandlePlaybackStateChanged(_playbackBackend.State);
        HandleTracksChanged(_playbackBackend.CurrentTracks);
        HandleClockChanged(_playbackBackend.Clock.Current);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _playbackBackend.StateChanged -= HandlePlaybackStateChanged;
        _playbackBackend.TracksChanged -= HandleTracksChanged;
        _playbackBackend.Clock.Changed -= HandleClockChanged;
        _disposed = true;
    }

    private void HandlePlaybackStateChanged(PlaybackBackendState state)
    {
        _mediaSessionCoordinator.ApplyPlaybackState(state);
    }

    private void HandleTracksChanged(IReadOnlyList<Core.MediaTrackInfo> tracks)
    {
        _mediaSessionCoordinator.ApplyTracks(tracks);
    }

    private void HandleClockChanged(ClockSnapshot clock)
    {
        _mediaSessionCoordinator.ApplyClock(clock);
    }
}
