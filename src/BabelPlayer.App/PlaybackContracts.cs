using BabelPlayer.Core;

namespace BabelPlayer.App;

public readonly record struct ClockSnapshot(
    TimeSpan Position,
    TimeSpan Duration,
    double Rate,
    bool IsPaused,
    bool IsSeekable,
    DateTimeOffset SampledAtUtc);

public interface IPlaybackClock
{
    event Action<ClockSnapshot>? Changed;

    ClockSnapshot Current { get; }
}

public sealed record PlaybackBackendState
{
    public string? Path { get; init; }
    public bool HasVideo { get; init; }
    public bool HasAudio { get; init; }
    public int VideoWidth { get; init; }
    public int VideoHeight { get; init; }
    public int VideoDisplayWidth { get; init; }
    public int VideoDisplayHeight { get; init; }
    public bool IsMuted { get; init; }
    public double Volume { get; init; }
    public string ActiveHardwareDecoder { get; init; } = string.Empty;
}

public interface IPlaybackBackend : IAsyncDisposable
{
    event Action<PlaybackBackendState>? StateChanged;
    event Action<IReadOnlyList<MediaTrackInfo>>? TracksChanged;
    event Action? MediaOpened;
    event Action? MediaEnded;
    event Action<string>? MediaFailed;
    event Action<RuntimeInstallProgress>? RuntimeInstallProgress;

    IPlaybackClock Clock { get; }
    PlaybackBackendState State { get; }
    IReadOnlyList<MediaTrackInfo> CurrentTracks { get; }
    HardwareDecodingMode HardwareDecodingMode { get; }

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
    Task SetHardwareDecodingModeAsync(HardwareDecodingMode mode, CancellationToken cancellationToken);
    Task SetZoomAsync(double zoom, CancellationToken cancellationToken);
    Task SetPanAsync(double x, double y, CancellationToken cancellationToken);
    Task ScreenshotAsync(string outputPath, CancellationToken cancellationToken);
}
