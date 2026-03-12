using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class ResumeTrackingCoordinator : IDisposable
{
    private readonly IPlaybackBackend _playbackBackend;
    private readonly ResumePlaybackService _resumePlaybackService;
    private readonly TimeSpan _saveInterval;
    private DateTimeOffset? _lastPersistedAtUtc;
    private string? _currentMediaPath;
    private bool _enabled;
    private bool _disposed;

    public ResumeTrackingCoordinator(
        IPlaybackBackend playbackBackend,
        ResumePlaybackService resumePlaybackService,
        TimeSpan? saveInterval = null)
    {
        _playbackBackend = playbackBackend;
        _resumePlaybackService = resumePlaybackService;
        _saveInterval = saveInterval ?? TimeSpan.FromSeconds(5);
        _playbackBackend.Clock.Changed += HandleClockChanged;
    }

    public PlaybackStateSnapshot CurrentSnapshot => BuildSnapshot(_playbackBackend.State, _playbackBackend.Clock.Current);

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
    }

    public void ResetForMedia(string? mediaPath)
    {
        if (string.Equals(_currentMediaPath, mediaPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentMediaPath = mediaPath;
        _lastPersistedAtUtc = null;
    }

    public void Flush(bool forceRemoveCompleted = false)
    {
        if (!_enabled)
        {
            return;
        }

        var snapshot = CurrentSnapshot;
        if (string.IsNullOrWhiteSpace(snapshot.Path))
        {
            return;
        }

        _resumePlaybackService.Update(snapshot, forceRemoveCompleted);
        _lastPersistedAtUtc = snapshot.Duration > TimeSpan.Zero
            ? _playbackBackend.Clock.Current.SampledAtUtc
            : _lastPersistedAtUtc;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _playbackBackend.Clock.Changed -= HandleClockChanged;
        _disposed = true;
    }

    private void HandleClockChanged(ClockSnapshot clock)
    {
        var snapshot = BuildSnapshot(_playbackBackend.State, clock);
        if (!string.Equals(_currentMediaPath, snapshot.Path, StringComparison.OrdinalIgnoreCase))
        {
            _currentMediaPath = snapshot.Path;
            _lastPersistedAtUtc = null;
        }

        if (!_enabled
            || string.IsNullOrWhiteSpace(snapshot.Path)
            || snapshot.Duration <= TimeSpan.Zero)
        {
            return;
        }

        if (!_lastPersistedAtUtc.HasValue || clock.SampledAtUtc - _lastPersistedAtUtc.Value >= _saveInterval)
        {
            _resumePlaybackService.Update(snapshot);
            _lastPersistedAtUtc = clock.SampledAtUtc;
        }
    }

    private static PlaybackStateSnapshot BuildSnapshot(PlaybackBackendState state, ClockSnapshot clock)
    {
        return new PlaybackStateSnapshot
        {
            Path = state.Path,
            Position = clock.Position,
            Duration = clock.Duration,
            VideoWidth = state.VideoWidth,
            VideoHeight = state.VideoHeight,
            VideoDisplayWidth = state.VideoDisplayWidth,
            VideoDisplayHeight = state.VideoDisplayHeight,
            IsPaused = clock.IsPaused,
            IsMuted = state.IsMuted,
            Volume = state.Volume,
            Speed = clock.Rate,
            HasVideo = state.HasVideo,
            HasAudio = state.HasAudio,
            IsSeekable = clock.IsSeekable,
            ActiveHardwareDecoder = state.ActiveHardwareDecoder
        };
    }
}
