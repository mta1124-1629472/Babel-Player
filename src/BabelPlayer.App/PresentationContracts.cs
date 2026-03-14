using BabelPlayer.Core;

namespace BabelPlayer.App;

public readonly record struct ShellStageBounds(int X, int Y, int Width, int Height);

public interface IVideoPresenter
{
    event Action? InputActivity;
    event Action? FullscreenExitRequested;
    event Func<object, bool>? ShortcutKeyPressed;

    object View { get; }

    void Initialize(object ownerWindow, IPlaybackHostRuntime playbackRuntime);
    void RequestBoundsSync();
    IDisposable SuppressPresentation();
    ShellStageBounds GetStageBounds(object relativeTo);
}

public interface ISubtitlePresenter
{
    void Hide();
    void ApplyStyle(ShellSubtitleStyle style);
    void Present(SubtitlePresentationModel model, ShellStageBounds stageBounds, int bottomOffset);
}
