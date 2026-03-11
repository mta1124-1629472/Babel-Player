using BabelPlayer.App;
using BabelPlayer.Core;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace BabelPlayer.WinUI;

public sealed class DetachedWindowSubtitlePresenter : ISubtitlePresenter, IDisposable
{
    private readonly Window _ownerWindow;
    private SubtitleOverlayWindow? _overlayWindow;
    private SubtitleStyleSettings _style = new();

    public DetachedWindowSubtitlePresenter(Window ownerWindow)
    {
        _ownerWindow = ownerWindow;
    }

    public void Hide()
    {
        _overlayWindow?.HideOverlay();
    }

    public void ApplyStyle(SubtitleStyleSettings style)
    {
        _style = style;
        _overlayWindow?.ApplyStyle(style);
    }

    public void Present(SubtitlePresentationModel model, RectInt32 stageBounds, int bottomOffset)
    {
        if (!model.IsVisible || stageBounds.Width <= 0 || stageBounds.Height <= 0)
        {
            Hide();
            return;
        }

        EnsureWindow();
        _overlayWindow!.ApplyStyle(_style);
        _overlayWindow.SetContent(
            model.SecondaryText,
            model.PrimaryText,
            !string.IsNullOrWhiteSpace(model.SecondaryText),
            !string.IsNullOrWhiteSpace(model.PrimaryText));
        _overlayWindow.ShowOverlay(stageBounds, bottomOffset);
    }

    public void Dispose()
    {
        _overlayWindow?.CloseOverlay();
        _overlayWindow = null;
    }

    private void EnsureWindow()
    {
        _overlayWindow ??= new SubtitleOverlayWindow(WinRT.Interop.WindowNative.GetWindowHandle(_ownerWindow));
    }
}
