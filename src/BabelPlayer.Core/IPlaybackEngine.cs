namespace BabelPlayer.Core;

public interface IPlaybackEngine : IAsyncDisposable
{
    event Action<PlaybackStateSnapshot>? OnStateChanged;
    event Action<IReadOnlyList<MediaTrackInfo>>? OnTracksChanged;
    event Action? OnMediaOpened;
    event Action? OnMediaEnded;
    event Action<string>? OnMediaFailed;

    PlaybackStateSnapshot Snapshot { get; }

    Task InitializeAsync(nint hostHandle, HardwareDecodingMode hardwareDecodingMode, CancellationToken cancellationToken);
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
    Task SetZoomAsync(double zoom, CancellationToken cancellationToken);
    Task SetPanAsync(double x, double y, CancellationToken cancellationToken);
    Task ScreenshotAsync(string outputPath, CancellationToken cancellationToken);
}
