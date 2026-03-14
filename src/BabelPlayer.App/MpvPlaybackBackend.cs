using BabelPlayer.Core;

namespace BabelPlayer.App;

using System.Diagnostics;

public sealed class MpvPlaybackBackend : IPlaybackBackend
{
    private readonly MpvPlaybackEngine _engine;
    private readonly BackendPlaybackClock _clock = new();
    private readonly IBabelLogger _logger;
    private PlaybackBackendState _state = new();

    public MpvPlaybackBackend(IRuntimeBootstrapService runtimeBootstrapService, IBabelLogFactory? logFactory = null)
    {
        var effectiveLogFactory = logFactory ?? NullBabelLogFactory.Instance;
        _logger = effectiveLogFactory.CreateLogger("playback.backend");
        _engine = new MpvPlaybackEngine(runtimeBootstrapService, effectiveLogFactory);
        _engine.OnStateChanged += HandleEngineStateChanged;
        _engine.OnTracksChanged += tracks => TracksChanged?.Invoke(tracks);
        _engine.OnMediaOpened += () =>
        {
            _logger.LogInfo("Media opened.", BabelLogContext.Create(("path", _state.Path)));
            MediaOpened?.Invoke();
        };
        _engine.OnMediaEnded += () =>
        {
            _logger.LogInfo("Media ended.", BabelLogContext.Create(("path", _state.Path)));
            MediaEnded?.Invoke();
        };
        _engine.OnMediaFailed += message =>
        {
            _logger.LogError("Media failed.", null, BabelLogContext.Create(("path", _state.Path), ("message", message)));
            MediaFailed?.Invoke(message);
        };
        _engine.OnRuntimeInstallProgress += progress => RuntimeInstallProgress?.Invoke(progress);
    }

    public event Action<PlaybackBackendState>? StateChanged;
    public event Action<IReadOnlyList<MediaTrackInfo>>? TracksChanged;
    public event Action? MediaOpened;
    public event Action? MediaEnded;
    public event Action<string>? MediaFailed;
    public event Action<RuntimeInstallProgress>? RuntimeInstallProgress;

    public IPlaybackClock Clock => _clock;
    public PlaybackBackendState State => _state;
    public IReadOnlyList<MediaTrackInfo> CurrentTracks => _engine.CurrentTracks;
    public HardwareDecodingMode HardwareDecodingMode { get; private set; } = HardwareDecodingMode.AutoSafe;

    public Task InitializeAsync(nint hostHandle, CancellationToken cancellationToken)
    {
        _logger.LogInfo("Initializing playback backend.", BabelLogContext.Create(("hostHandle", hostHandle)));
        using var activity = BabelTracing.Source.StartActivity("playback.initialize");
        activity?.SetTag(BabelTracing.Tags.BackendHandle, hostHandle.ToString());
        return _engine.InitializeAsync(hostHandle, HardwareDecodingMode, cancellationToken);
    }

    public async Task LoadAsync(string path, CancellationToken cancellationToken)
    {
        using var activity = BabelTracing.Source.StartActivity("playback.load_media");
        activity?.SetTag(BabelTracing.Tags.MediaPath, path);
        _logger.LogInfo("Loading media into playback backend.", BabelLogContext.Create(("path", path)));
        await _engine.LoadAsync(path, cancellationToken);
    }
    public Task PlayAsync(CancellationToken cancellationToken) => _engine.PlayAsync(cancellationToken);
    public Task PauseAsync(CancellationToken cancellationToken) => _engine.PauseAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => _engine.StopAsync(cancellationToken);
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) => _engine.SeekAsync(position, cancellationToken);
    public Task SeekRelativeAsync(TimeSpan delta, CancellationToken cancellationToken) => _engine.SeekRelativeAsync(delta, cancellationToken);
    public Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken) => _engine.SetPlaybackRateAsync(speed, cancellationToken);
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) => _engine.SetVolumeAsync(volume, cancellationToken);
    public Task SetMuteAsync(bool muted, CancellationToken cancellationToken) => _engine.SetMuteAsync(muted, cancellationToken);
    public Task StepFrameAsync(bool forward, CancellationToken cancellationToken) => _engine.StepFrameAsync(forward, cancellationToken);
    public Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken) => _engine.SetAudioTrackAsync(trackId, cancellationToken);
    public Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken) => _engine.SetSubtitleTrackAsync(trackId, cancellationToken);
    public Task SetAudioDelayAsync(double seconds, CancellationToken cancellationToken) => _engine.SetAudioDelayAsync(seconds, cancellationToken);
    public Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken) => _engine.SetSubtitleDelayAsync(seconds, cancellationToken);
    public Task SetAspectRatioAsync(string aspectRatio, CancellationToken cancellationToken) => _engine.SetAspectRatioAsync(aspectRatio, cancellationToken);

    public Task SetHardwareDecodingModeAsync(HardwareDecodingMode mode, CancellationToken cancellationToken)
    {
        HardwareDecodingMode = mode;
        return _engine.SetHardwareDecodingModeAsync(mode, cancellationToken);
    }

    public Task SetZoomAsync(double zoom, CancellationToken cancellationToken) => _engine.SetZoomAsync(zoom, cancellationToken);
    public Task SetPanAsync(double x, double y, CancellationToken cancellationToken) => _engine.SetPanAsync(x, y, cancellationToken);
    public Task ScreenshotAsync(string outputPath, CancellationToken cancellationToken) => _engine.ScreenshotAsync(outputPath, cancellationToken);
    public ValueTask DisposeAsync() => _engine.DisposeAsync();

    private void HandleEngineStateChanged(PlaybackStateSnapshot snapshot)
    {
        _clock.Update(new ClockSnapshot(
            snapshot.Position,
            snapshot.Duration,
            snapshot.Speed,
            snapshot.IsPaused,
            snapshot.IsSeekable,
            DateTimeOffset.UtcNow));

        _state = new PlaybackBackendState
        {
            Path = snapshot.Path,
            HasVideo = snapshot.HasVideo,
            HasAudio = snapshot.HasAudio,
            VideoWidth = snapshot.VideoWidth,
            VideoHeight = snapshot.VideoHeight,
            VideoDisplayWidth = snapshot.VideoDisplayWidth,
            VideoDisplayHeight = snapshot.VideoDisplayHeight,
            IsMuted = snapshot.IsMuted,
            Volume = snapshot.Volume,
            ActiveHardwareDecoder = snapshot.ActiveHardwareDecoder
        };
        StateChanged?.Invoke(_state);
    }

    private sealed class BackendPlaybackClock : IPlaybackClock
    {
        private ClockSnapshot _current = new(TimeSpan.Zero, TimeSpan.Zero, 1.0, true, false, DateTimeOffset.UtcNow);

        public event Action<ClockSnapshot>? Changed;

        public ClockSnapshot Current => _current;

        public void Update(ClockSnapshot snapshot)
        {
            _current = snapshot;
            Changed?.Invoke(snapshot);
        }
    }
}
