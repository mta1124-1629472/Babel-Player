using BabelPlayer.App;
using BabelPlayer.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace BabelPlayer.WinUI;

public sealed class WinUIWindowModeService : IWindowModeService
{
    private readonly AppWindow _appWindow;
    private PlaybackWindowMode _mode = PlaybackWindowMode.Standard;
    private PlaybackWindowMode _lastWindowedMode = PlaybackWindowMode.Standard;

    public WinUIWindowModeService(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
    }

    public PlaybackWindowMode CurrentMode => _mode;

    public Task SetModeAsync(PlaybackWindowMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (mode)
        {
            case PlaybackWindowMode.Standard:
                _appWindow.SetPresenter(AppWindowPresenterKind.Default);
                break;
            case PlaybackWindowMode.Borderless:
                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                if (_appWindow.Presenter is OverlappedPresenter overlappedPresenter)
                {
                    overlappedPresenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
                    overlappedPresenter.IsResizable = true;
                    overlappedPresenter.IsMaximizable = true;
                    overlappedPresenter.IsMinimizable = true;
                }

                break;
            case PlaybackWindowMode.PictureInPicture:
                _appWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);
                break;
        }

        _mode = mode;
        if (mode is PlaybackWindowMode.Standard or PlaybackWindowMode.Borderless or PlaybackWindowMode.PictureInPicture)
        {
            _lastWindowedMode = mode;
        }

        return Task.CompletedTask;
    }

    public Task EnterFullscreenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        return Task.CompletedTask;
    }

    public Task ExitFullscreenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SetModeAsync(_lastWindowedMode, cancellationToken);
    }
}
