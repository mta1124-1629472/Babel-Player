using BabelPlayer.App;
using BabelPlayer.Core;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace BabelPlayer.WinUI;

public interface IVideoPresenter
{
    event Action? InputActivity;
    event Action? FullscreenExitRequested;
    event Func<ShortcutKeyInput, bool>? ShortcutKeyPressed;

    FrameworkElement View { get; }

    void Initialize(Window ownerWindow, IPlaybackBackend playbackBackend);
    void RequestBoundsSync();
    IDisposable SuppressPresentation();
    RectInt32 GetStageBounds(FrameworkElement relativeTo);
}

public interface ISubtitlePresenter
{
    void Hide();
    void ApplyStyle(SubtitleStyleSettings style);
    void Present(SubtitlePresentationModel model, RectInt32 stageBounds, int bottomOffset);
}
