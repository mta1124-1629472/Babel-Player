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

        _playbackBackend.MediaOpened += () => MediaOpened?.Invoke(BuildSnapshot());
        _playbackBackend.MediaEnded += () => MediaEnded?.Invoke(BuildSnapshot());
        _playbackBackend.MediaFailed += message => MediaFailed?.Invoke(message);
        _playbackBackend.TracksChanged += tracks => TracksChanged?.Invoke(tracks);
        _playbackBackend.RuntimeInstallProgress += progress => RuntimeInstallProgress?.Invoke(progress);
        _playbackBackend.StateChanged += _ => PlaybackStateChanged?.Invoke(BuildSnapshot());
        _playbackBackend.Clock.Changed += _ => PlaybackStateChanged?.Invoke(BuildSnapshot());

        _videoPresenter.InputActivity += () => InputActivity?.Invoke();
        _videoPresenter.FullscreenExitRequested += () => FullscreenExitRequested?.Invoke();
        _videoPresenter.ShortcutKeyPressed += HandleShortcutKeyPressed;
    }

    public event Action<PlaybackStateSnapshot>? MediaOpened;
    public event Action<PlaybackStateSnapshot>? MediaEnded;
    public event Action<string>? MediaFailed;
    public event Action<IReadOnlyList<MediaTrackInfo>>? TracksChanged;
    public event Action<RuntimeInstallProgress>? RuntimeInstallProgress;
    public event Action<PlaybackStateSnapshot>? PlaybackStateChanged;
    public event Action? InputActivity;
    public event Action? FullscreenExitRequested;
    public event Func<ShortcutKeyInput, bool>? ShortcutKeyPressed;

    public FrameworkElement View => _videoPresenter.View;

    public void Initialize(Window ownerWindow)
    {
        _videoPresenter.Initialize(ownerWindow, _playbackBackend);
    }

    public void RequestHostBoundsSync() => _videoPresenter.RequestBoundsSync();

    public IDisposable SuppressNativeHost() => _videoPresenter.SuppressPresentation();

    public RectInt32 GetStageBounds(FrameworkElement relativeTo) => _videoPresenter.GetStageBounds(relativeTo);

    private PlaybackStateSnapshot BuildSnapshot()
    {
        return new PlaybackStateSnapshot
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
