using BabelPlayer.App;
using BabelPlayer.Core;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace BabelPlayer.WinUI;

public sealed class PlaybackHostAdapter
{
    private readonly IPlaybackBackend _playbackBackend;
    private readonly IVideoPresenter _videoPresenter;

    public PlaybackHostAdapter(IPlaybackBackend playbackBackend, IVideoPresenter videoPresenter)
    {
        _playbackBackend = playbackBackend;
        _videoPresenter = videoPresenter;

        _playbackBackend.MediaOpened += () => MediaOpened?.Invoke();
        _playbackBackend.MediaEnded += () => MediaEnded?.Invoke();
        _playbackBackend.MediaFailed += message => MediaFailed?.Invoke(message);
        _playbackBackend.TracksChanged += tracks => TracksChanged?.Invoke(tracks);
        _playbackBackend.RuntimeInstallProgress += progress => RuntimeInstallProgress?.Invoke(progress);
        _playbackBackend.StateChanged += _ => PlaybackStateChanged?.Invoke(Snapshot);
        _playbackBackend.Clock.Changed += _ => PlaybackStateChanged?.Invoke(Snapshot);

        _videoPresenter.InputActivity += () => InputActivity?.Invoke();
        _videoPresenter.FullscreenExitRequested += () => FullscreenExitRequested?.Invoke();
        _videoPresenter.ShortcutKeyPressed += HandleShortcutKeyPressed;
    }

    public event Action? MediaOpened;
    public event Action? MediaEnded;
    public event Action<string>? MediaFailed;
    public event Action<IReadOnlyList<MediaTrackInfo>>? TracksChanged;
    public event Action<RuntimeInstallProgress>? RuntimeInstallProgress;
    public event Action<PlaybackStateSnapshot>? PlaybackStateChanged;
    public event Action? InputActivity;
    public event Action? FullscreenExitRequested;
    public event Func<ShortcutKeyInput, bool>? ShortcutKeyPressed;

    public FrameworkElement View => _videoPresenter.View;

    public Uri? Source
    {
        get => string.IsNullOrWhiteSpace(_playbackBackend.State.Path) ? null : new Uri(_playbackBackend.State.Path);
        set
        {
            if (value is null)
            {
                _ = _playbackBackend.StopAsync(CancellationToken.None);
                return;
            }

            _ = _playbackBackend.LoadAsync(value.LocalPath, CancellationToken.None);
        }
    }

    public TimeSpan Position
    {
        get => _playbackBackend.Clock.Current.Position;
        set => _ = _playbackBackend.SeekAsync(value, CancellationToken.None);
    }

    public double Volume
    {
        get => _playbackBackend.State.Volume;
        set => _ = _playbackBackend.SetVolumeAsync(value, CancellationToken.None);
    }

    public Duration NaturalDuration => _playbackBackend.Clock.Current.Duration > TimeSpan.Zero
        ? new Duration(_playbackBackend.Clock.Current.Duration)
        : Duration.Automatic;

    public PlaybackStateSnapshot Snapshot => new()
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

    public double PlaybackRate => _playbackBackend.Clock.Current.Rate;

    public bool IsMuted => _playbackBackend.State.IsMuted;

    public bool IsPaused => _playbackBackend.Clock.Current.IsPaused;

    public IReadOnlyList<MediaTrackInfo> CurrentTracks => _playbackBackend.CurrentTracks;

    public string ActiveHardwareDecoder => _playbackBackend.State.ActiveHardwareDecoder;

    public HardwareDecodingMode HardwareDecodingMode => _playbackBackend.HardwareDecodingMode;

    public void Initialize(Window ownerWindow)
    {
        _videoPresenter.Initialize(ownerWindow, _playbackBackend);
    }

    public void Play() => _ = _playbackBackend.PlayAsync(CancellationToken.None);

    public Task PlayAsync() => _playbackBackend.PlayAsync(CancellationToken.None);

    public void Pause() => _ = _playbackBackend.PauseAsync(CancellationToken.None);

    public Task PauseAsync() => _playbackBackend.PauseAsync(CancellationToken.None);

    public void Stop() => _ = _playbackBackend.StopAsync(CancellationToken.None);

    public void SeekBy(TimeSpan delta) => _ = _playbackBackend.SeekRelativeAsync(delta, CancellationToken.None);

    public void StepFrame(bool forward) => _ = _playbackBackend.StepFrameAsync(forward, CancellationToken.None);

    public void SetPlaybackRate(double speed) => _ = _playbackBackend.SetPlaybackRateAsync(speed, CancellationToken.None);

    public void SetMute(bool muted) => _ = _playbackBackend.SetMuteAsync(muted, CancellationToken.None);

    public void SelectAudioTrack(int? trackId) => _ = _playbackBackend.SetAudioTrackAsync(trackId, CancellationToken.None);

    public void SelectSubtitleTrack(int? trackId) => _ = _playbackBackend.SetSubtitleTrackAsync(trackId, CancellationToken.None);

    public void SetAudioDelay(double seconds) => _ = _playbackBackend.SetAudioDelayAsync(seconds, CancellationToken.None);

    public void SetSubtitleDelay(double seconds) => _ = _playbackBackend.SetSubtitleDelayAsync(seconds, CancellationToken.None);

    public void SetAspectRatio(string aspectRatio) => _ = _playbackBackend.SetAspectRatioAsync(aspectRatio, CancellationToken.None);

    public void SetHardwareDecodingMode(HardwareDecodingMode mode) => _ = _playbackBackend.SetHardwareDecodingModeAsync(mode, CancellationToken.None);

    public void SetZoom(double zoom) => _ = _playbackBackend.SetZoomAsync(zoom, CancellationToken.None);

    public void SetPan(double x, double y) => _ = _playbackBackend.SetPanAsync(x, y, CancellationToken.None);

    public void Screenshot(string outputPath) => _ = _playbackBackend.ScreenshotAsync(outputPath, CancellationToken.None);

    public void RequestHostBoundsSync() => _videoPresenter.RequestBoundsSync();

    public IDisposable SuppressNativeHost() => _videoPresenter.SuppressPresentation();

    public RectInt32 GetStageBounds(FrameworkElement relativeTo) => _videoPresenter.GetStageBounds(relativeTo);

    private bool HandleShortcutKeyPressed(ShortcutKeyInput input)
    {
        if (ShortcutKeyPressed is null)
        {
            return false;
        }

        foreach (Func<ShortcutKeyInput, bool> handler in ShortcutKeyPressed.GetInvocationList())
        {
            if (handler(input))
            {
                return true;
            }
        }

        return false;
    }
}
