using BabelPlayer.App;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace BabelPlayer.WinUI;

public sealed class PlaybackHostAdapter
{
    private readonly IPlaybackHostRuntime _playbackRuntime;
    private readonly IVideoPresenter _videoPresenter;

    public PlaybackHostAdapter(IPlaybackHostRuntime playbackRuntime, IVideoPresenter videoPresenter)
    {
        _playbackRuntime = playbackRuntime;
        _videoPresenter = videoPresenter;

        _playbackRuntime.MediaOpened += snapshot => MediaOpened?.Invoke(snapshot);
        _playbackRuntime.MediaEnded += snapshot => MediaEnded?.Invoke(snapshot);
        _playbackRuntime.MediaFailed += message => MediaFailed?.Invoke(message);
        _playbackRuntime.TracksChanged += tracks => TracksChanged?.Invoke(tracks);
        _playbackRuntime.RuntimeInstallProgress += progress => RuntimeInstallProgress?.Invoke(progress);
        _playbackRuntime.PlaybackStateChanged += snapshot => PlaybackStateChanged?.Invoke(snapshot);

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
        _videoPresenter.Initialize(ownerWindow, _playbackRuntime);
    }

    public void RequestHostBoundsSync() => _videoPresenter.RequestBoundsSync();

    public IDisposable SuppressNativeHost() => _videoPresenter.SuppressPresentation();

    public RectInt32 GetStageBounds(FrameworkElement relativeTo) => _videoPresenter.GetStageBounds(relativeTo);

    public void SetPreferredAudioState(double volume, bool muted)
    {
        if (View is MpvHostControl hostControl)
        {
            hostControl.SetPreferredAudioState(volume, muted);
        }
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
