using BabelPlayer.App;
using BabelPlayer.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace BabelPlayer.WinUI;

public sealed class WinUIWindowModeService : IWindowModeService
{
    private readonly AppWindow _appWindow;
    private PlaybackWindowMode _mode = PlaybackWindowMode.Standard;
    private RectInt32? _standardBounds;

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
                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                if (_appWindow.Presenter is OverlappedPresenter standardPresenter)
                {
                    standardPresenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true);
                    standardPresenter.IsResizable = true;
                    standardPresenter.IsMaximizable = true;
                    standardPresenter.IsMinimizable = true;
                }

                if (_standardBounds.HasValue)
                {
                    _appWindow.MoveAndResize(_standardBounds.Value);
                }

                break;
            case PlaybackWindowMode.Borderless:
                CaptureStandardBounds();
                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                if (_appWindow.Presenter is OverlappedPresenter overlappedPresenter)
                {
                    overlappedPresenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
                    overlappedPresenter.IsResizable = false;
                    overlappedPresenter.IsMaximizable = false;
                    overlappedPresenter.IsMinimizable = true;
                }

                var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
                _appWindow.MoveAndResize(displayArea.WorkArea);
                break;
            case PlaybackWindowMode.PictureInPicture:
                CaptureStandardBounds();
                _appWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);
                break;
            case PlaybackWindowMode.Fullscreen:
                CaptureStandardBounds();
                _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                break;
        }

        _mode = mode;
        return Task.CompletedTask;
    }

    public Task EnterFullscreenAsync(CancellationToken cancellationToken = default)
    {
        return SetModeAsync(PlaybackWindowMode.Fullscreen, cancellationToken);
    }

    public Task ExitFullscreenAsync(CancellationToken cancellationToken = default)
    {
        return SetModeAsync(PlaybackWindowMode.Standard, cancellationToken);
    }

    private void CaptureStandardBounds()
    {
        if (_mode != PlaybackWindowMode.Standard)
        {
            return;
        }

        _standardBounds = new RectInt32(
            _appWindow.Position.X,
            _appWindow.Position.Y,
            _appWindow.Size.Width,
            _appWindow.Size.Height);
    }
}
