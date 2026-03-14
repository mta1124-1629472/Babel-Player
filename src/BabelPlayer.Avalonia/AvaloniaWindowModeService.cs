using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public sealed class AvaloniaWindowModeService : IWindowModeService
{
    private readonly Window _window;
    private PixelPoint _standardPosition;
    private Size _standardSize;
    private WindowState _standardState;

    public AvaloniaWindowModeService(Window window)
    {
        _window = window;
        _standardPosition = window.Position;
        _standardSize = new Size(
            Math.Max(window.Width, 1280),
            Math.Max(window.Height, 720));
        _standardState = window.WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : window.WindowState;
    }

    public ShellPlaybackWindowMode CurrentMode { get; private set; } = ShellPlaybackWindowMode.Standard;

    public DisplayBounds GetCurrentDisplayBounds(bool workArea = false)
    {
        var screens = _window.Screens;
        var screen = screens?.ScreenFromWindow(_window) ?? screens?.Primary;
        if (screen is null)
        {
            return new DisplayBounds(
                _window.Position.X,
                _window.Position.Y,
                (int)Math.Max(_window.Width, 0),
                (int)Math.Max(_window.Height, 0));
        }

        var rect = workArea ? screen.WorkingArea : screen.Bounds;
        return new DisplayBounds(rect.X, rect.Y, rect.Width, rect.Height);
    }

    public Task SetModeAsync(ShellPlaybackWindowMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (mode)
        {
            case ShellPlaybackWindowMode.Fullscreen:
                CaptureStandardBounds();
                _window.SystemDecorations = SystemDecorations.None;
                _window.WindowState = WindowState.FullScreen;
                CurrentMode = ShellPlaybackWindowMode.Fullscreen;
                break;

            case ShellPlaybackWindowMode.Borderless:
                CaptureStandardBounds();
                _window.WindowState = WindowState.Normal;
                _window.SystemDecorations = SystemDecorations.None;
                CurrentMode = ShellPlaybackWindowMode.Borderless;
                break;

            case ShellPlaybackWindowMode.PictureInPicture:
                CaptureStandardBounds();
                _window.WindowState = WindowState.Normal;
                _window.SystemDecorations = SystemDecorations.None;
                CurrentMode = ShellPlaybackWindowMode.PictureInPicture;
                break;

            case ShellPlaybackWindowMode.Standard:
            default:
                RestoreStandardBounds();
                CurrentMode = ShellPlaybackWindowMode.Standard;
                break;
        }

        return Task.CompletedTask;
    }

    private void CaptureStandardBounds()
    {
        if (CurrentMode == ShellPlaybackWindowMode.Fullscreen)
        {
            return;
        }

        _standardPosition = _window.Position;
        _standardSize = new Size(
            Math.Max(_window.Width, 640),
            Math.Max(_window.Height, 480));
        _standardState = _window.WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : _window.WindowState;
    }

    private void RestoreStandardBounds()
    {
        _window.WindowState = WindowState.Normal;
        _window.SystemDecorations = SystemDecorations.Full;
        _window.Position = _standardPosition;
        _window.Width = _standardSize.Width;
        _window.Height = _standardSize.Height;

        if (_standardState == WindowState.Maximized)
        {
            _window.WindowState = WindowState.Maximized;
        }
    }
}
