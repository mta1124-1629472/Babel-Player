using BabelPlayer.App;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace BabelPlayer.WinUI;

public sealed class MpvVideoPresenter : IVideoPresenter
{
    private readonly MpvHostControl _hostControl;

    public MpvVideoPresenter(IBabelLogFactory? logFactory = null)
    {
        _hostControl = new MpvHostControl(logFactory)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    public event Action? InputActivity
    {
        add => _hostControl.InputActivity += value;
        remove => _hostControl.InputActivity -= value;
    }

    public event Action? FullscreenExitRequested
    {
        add => _hostControl.FullscreenExitRequested += value;
        remove => _hostControl.FullscreenExitRequested -= value;
    }

    public event Func<ShortcutKeyInput, bool>? ShortcutKeyPressed
    {
        add => _hostControl.ShortcutKeyPressed += value;
        remove => _hostControl.ShortcutKeyPressed -= value;
    }

    public FrameworkElement View => _hostControl;

    public void Initialize(Window ownerWindow, IPlaybackHostRuntime playbackRuntime)
    {
        _hostControl.Initialize(ownerWindow, playbackRuntime);
    }

    public void RequestBoundsSync()
    {
        _hostControl.RequestHostBoundsSync();
    }

    public IDisposable SuppressPresentation()
    {
        return _hostControl.SuppressNativeHost();
    }

    public RectInt32 GetStageBounds(FrameworkElement relativeTo)
    {
        return _hostControl.GetStageBounds(relativeTo);
    }
}
