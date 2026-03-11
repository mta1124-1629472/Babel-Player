using BabelPlayer.App;
using BabelPlayer.Core;
using System.IO;
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
    private bool _initialStandardBoundsApplied;

    public WinUIWindowModeService(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
    }

    public PlaybackWindowMode CurrentMode => _mode;
    public RectInt32 CurrentBounds => new(_appWindow.Position.X, _appWindow.Position.Y, _appWindow.Size.Width, _appWindow.Size.Height);

    public void SetWindowIcon(string iconPath)
    {
        if (!File.Exists(iconPath))
        {
            return;
        }

        _appWindow.SetIcon(iconPath);
    }

    public void EnsureInitialStandardBounds()
    {
        if (_initialStandardBoundsApplied || _standardBounds.HasValue)
        {
            return;
        }

        var workArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        const int minWidth = 1100;
        const int minHeight = 700;

        var targetWidth = (int)Math.Round(workArea.Width * 0.78);
        var targetHeight = (int)Math.Round(targetWidth * 10d / 16d);

        var maxWidth = Math.Max(minWidth, (int)Math.Round(workArea.Width * 0.92));
        var maxHeight = Math.Max(minHeight, (int)Math.Round(workArea.Height * 0.92));
        targetWidth = Math.Clamp(targetWidth, minWidth, Math.Min(workArea.Width, maxWidth));
        targetHeight = Math.Clamp(targetHeight, minHeight, Math.Min(workArea.Height, maxHeight));

        if (targetHeight > workArea.Height)
        {
            targetHeight = workArea.Height;
            targetWidth = (int)Math.Round(targetHeight * 16d / 10d);
            targetWidth = Math.Clamp(targetWidth, minWidth, workArea.Width);
        }

        var x = workArea.X + Math.Max((workArea.Width - targetWidth) / 2, 0);
        var y = workArea.Y + Math.Max((workArea.Height - targetHeight) / 2, 0);

        _standardBounds = new RectInt32(x, y, targetWidth, targetHeight);
        _appWindow.MoveAndResize(_standardBounds.Value);
        _initialStandardBoundsApplied = true;
    }

    public RectInt32 GetCurrentDisplayBounds(bool workArea = false)
    {
        var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        return workArea ? displayArea.WorkArea : displayArea.OuterBounds;
    }

    public void ApplyStandardBounds(RectInt32 bounds)
    {
        _standardBounds = bounds;
        _initialStandardBoundsApplied = true;
        if (_mode == PlaybackWindowMode.Standard)
        {
            _appWindow.MoveAndResize(bounds);
        }
    }

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

                if (!_initialStandardBoundsApplied && !_standardBounds.HasValue)
                {
                    EnsureInitialStandardBounds();
                }
                else if (_standardBounds.HasValue)
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
