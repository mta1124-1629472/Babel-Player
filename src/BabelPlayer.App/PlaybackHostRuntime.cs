namespace BabelPlayer.App;

public interface IPlaybackHostRuntime
{
    event Action<ShellPlaybackStateSnapshot>? MediaOpened;
    event Action<ShellPlaybackStateSnapshot>? MediaEnded;
    event Action<string>? MediaFailed;
    event Action<IReadOnlyList<ShellMediaTrack>>? TracksChanged;
    event Action<ShellRuntimeInstallProgress>? RuntimeInstallProgress;
    event Action<ShellPlaybackStateSnapshot>? PlaybackStateChanged;

    ShellPlaybackStateSnapshot Current { get; }
    IReadOnlyList<ShellMediaTrack> CurrentTracks { get; }
    ShellHardwareDecodingMode HardwareDecodingMode { get; set; }

    Task InitializeAsync(nint hostHandle, CancellationToken cancellationToken);
    Task LoadAsync(string path, CancellationToken cancellationToken);
    Task PlayAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);
    Task SeekRelativeAsync(TimeSpan delta, CancellationToken cancellationToken);
    Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken);
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken);
    Task SetMuteAsync(bool muted, CancellationToken cancellationToken);
    Task StepFrameAsync(bool forward, CancellationToken cancellationToken);
    Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken);
    Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken);
    Task SetAudioDelayAsync(double seconds, CancellationToken cancellationToken);
    Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken);
    Task SetAspectRatioAsync(string aspectRatio, CancellationToken cancellationToken);
    Task SetHardwareDecodingModeAsync(ShellHardwareDecodingMode mode, CancellationToken cancellationToken);
    Task SetZoomAsync(double zoom, CancellationToken cancellationToken);
    Task SetPanAsync(double x, double y, CancellationToken cancellationToken);
    Task ScreenshotAsync(string outputPath, CancellationToken cancellationToken);
}

public sealed class PlaybackHostRuntimeAdapter : IPlaybackHostRuntime, IDisposable
{
    private readonly IPlaybackBackend _playbackBackend;

    public PlaybackHostRuntimeAdapter(IPlaybackBackend playbackBackend)
    {
        _playbackBackend = playbackBackend;
        _playbackBackend.MediaOpened += HandleMediaOpened;
        _playbackBackend.MediaEnded += HandleMediaEnded;
        _playbackBackend.MediaFailed += HandleMediaFailed;
        _playbackBackend.TracksChanged += HandleTracksChanged;
        _playbackBackend.RuntimeInstallProgress += HandleRuntimeInstallProgress;
        _playbackBackend.StateChanged += HandlePlaybackStateChanged;
        _playbackBackend.Clock.Changed += HandleClockChanged;
    }

    public event Action<ShellPlaybackStateSnapshot>? MediaOpened;
    public event Action<ShellPlaybackStateSnapshot>? MediaEnded;
    public event Action<string>? MediaFailed;
    public event Action<IReadOnlyList<ShellMediaTrack>>? TracksChanged;
    public event Action<ShellRuntimeInstallProgress>? RuntimeInstallProgress;
    public event Action<ShellPlaybackStateSnapshot>? PlaybackStateChanged;

    public ShellPlaybackStateSnapshot Current => BuildSnapshot();

    public IReadOnlyList<ShellMediaTrack> CurrentTracks => _playbackBackend.CurrentTracks.Select(track => track.ToShell()).ToArray();

    public ShellHardwareDecodingMode HardwareDecodingMode
    {
        get => _playbackBackend.HardwareDecodingMode.ToShell();
        set => _ = _playbackBackend.SetHardwareDecodingModeAsync(value.ToCore(), CancellationToken.None);
    }

    public Task InitializeAsync(nint hostHandle, CancellationToken cancellationToken)
        => _playbackBackend.InitializeAsync(hostHandle, cancellationToken);

    public Task LoadAsync(string path, CancellationToken cancellationToken)
        => _playbackBackend.LoadAsync(path, cancellationToken);

    public Task PlayAsync(CancellationToken cancellationToken)
        => _playbackBackend.PlayAsync(cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken)
        => _playbackBackend.PauseAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
        => _playbackBackend.StopAsync(cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
        => _playbackBackend.SeekAsync(position, cancellationToken);

    public Task SeekRelativeAsync(TimeSpan delta, CancellationToken cancellationToken)
        => _playbackBackend.SeekRelativeAsync(delta, cancellationToken);

    public Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken)
        => _playbackBackend.SetPlaybackRateAsync(speed, cancellationToken);

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken)
        => _playbackBackend.SetVolumeAsync(volume, cancellationToken);

    public Task SetMuteAsync(bool muted, CancellationToken cancellationToken)
        => _playbackBackend.SetMuteAsync(muted, cancellationToken);

    public Task StepFrameAsync(bool forward, CancellationToken cancellationToken)
        => _playbackBackend.StepFrameAsync(forward, cancellationToken);

    public Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken)
        => _playbackBackend.SetAudioTrackAsync(trackId, cancellationToken);

    public Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken)
        => _playbackBackend.SetSubtitleTrackAsync(trackId, cancellationToken);

    public Task SetAudioDelayAsync(double seconds, CancellationToken cancellationToken)
        => _playbackBackend.SetAudioDelayAsync(seconds, cancellationToken);

    public Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken)
        => _playbackBackend.SetSubtitleDelayAsync(seconds, cancellationToken);

    public Task SetAspectRatioAsync(string aspectRatio, CancellationToken cancellationToken)
        => _playbackBackend.SetAspectRatioAsync(aspectRatio, cancellationToken);

    public Task SetHardwareDecodingModeAsync(ShellHardwareDecodingMode mode, CancellationToken cancellationToken)
        => _playbackBackend.SetHardwareDecodingModeAsync(mode.ToCore(), cancellationToken);

    public Task SetZoomAsync(double zoom, CancellationToken cancellationToken)
        => _playbackBackend.SetZoomAsync(zoom, cancellationToken);

    public Task SetPanAsync(double x, double y, CancellationToken cancellationToken)
        => _playbackBackend.SetPanAsync(x, y, cancellationToken);

    public Task ScreenshotAsync(string outputPath, CancellationToken cancellationToken)
        => _playbackBackend.ScreenshotAsync(outputPath, cancellationToken);

    public void Dispose()
    {
        _playbackBackend.MediaOpened -= HandleMediaOpened;
        _playbackBackend.MediaEnded -= HandleMediaEnded;
        _playbackBackend.MediaFailed -= HandleMediaFailed;
        _playbackBackend.TracksChanged -= HandleTracksChanged;
        _playbackBackend.RuntimeInstallProgress -= HandleRuntimeInstallProgress;
        _playbackBackend.StateChanged -= HandlePlaybackStateChanged;
        _playbackBackend.Clock.Changed -= HandleClockChanged;
    }

    private void HandleMediaOpened()
    {
        MediaOpened?.Invoke(BuildSnapshot());
    }

    private void HandleMediaEnded()
    {
        MediaEnded?.Invoke(BuildSnapshot());
    }

    private void HandleMediaFailed(string message)
    {
        MediaFailed?.Invoke(message);
    }

    private void HandleTracksChanged(IReadOnlyList<BabelPlayer.Core.MediaTrackInfo> tracks)
    {
        TracksChanged?.Invoke(tracks.Select(track => track.ToShell()).ToArray());
    }

    private void HandleRuntimeInstallProgress(RuntimeInstallProgress progress)
    {
        RuntimeInstallProgress?.Invoke(progress.ToShell());
    }

    private void HandlePlaybackStateChanged(PlaybackBackendState state)
    {
        PlaybackStateChanged?.Invoke(BuildSnapshot());
    }

    private void HandleClockChanged(ClockSnapshot snapshot)
    {
        PlaybackStateChanged?.Invoke(BuildSnapshot());
    }

    private ShellPlaybackStateSnapshot BuildSnapshot()
    {
        return new ShellPlaybackStateSnapshot
        {
            Path = _playbackBackend.State.Path,
            Position = _playbackBackend.Clock.Current.Position,
            Duration = _playbackBackend.Clock.Current.Duration,
            VideoWidth = _playbackBackend.State.VideoWidth,
            VideoHeight = _playbackBackend.State.VideoHeight,
            VideoDisplayWidth = _playbackBackend.State.VideoDisplayWidth,
            VideoDisplayHeight = _playbackBackend.State.VideoDisplayHeight,
            IsPaused = _playbackBackend.Clock.Current.IsPaused,
            IsMuted = _playbackBackend.State.IsMuted,
            Volume = _playbackBackend.State.Volume,
            Speed = _playbackBackend.Clock.Current.Rate,
            HasVideo = _playbackBackend.State.HasVideo,
            HasAudio = _playbackBackend.State.HasAudio,
            IsSeekable = _playbackBackend.Clock.Current.IsSeekable,
            ActiveHardwareDecoder = _playbackBackend.State.ActiveHardwareDecoder
        };
    }
}
