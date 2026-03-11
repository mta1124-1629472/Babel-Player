using System.Runtime.InteropServices;
using BabelPlayer.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace BabelPlayer.WinUI;

public sealed class SubtitleOverlayWindow : Window
{
    private readonly Border _rootBorder;
    private readonly StackPanel _stackPanel;
    private readonly TextBlock _sourceTextBlock;
    private readonly TextBlock _translationTextBlock;
    private readonly IntPtr _ownerHwnd;
    private AppWindow? _appWindow;
    private IntPtr _hwnd;
    private bool _isInitialized;

    public SubtitleOverlayWindow(IntPtr ownerHwnd)
    {
        _ownerHwnd = ownerHwnd;
        var host = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(10, 10, 10, 10)
        };

        _rootBorder = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(18, 12, 18, 14),
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(ColorHelper.FromArgb(168, 18, 23, 32)),
            MaxWidth = 880,
            MinWidth = 0
        };

        _stackPanel = new StackPanel { Spacing = 4 };
        _sourceTextBlock = new TextBlock
        {
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _stackPanel.Children.Add(_sourceTextBlock);

        _translationTextBlock = new TextBlock
        {
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _stackPanel.Children.Add(_translationTextBlock);

        _rootBorder.Child = _stackPanel;
        host.Children.Add(_rootBorder);
        Content = host;
        ApplyStyle(new SubtitleStyleSettings());
    }

    public bool IsOverlayVisible { get; private set; }

    public void SetContent(string sourceText, string translationText, bool showSource, bool showTranslation)
    {
        _sourceTextBlock.Text = sourceText;
        _sourceTextBlock.Visibility = showSource && !string.IsNullOrWhiteSpace(sourceText)
            ? Visibility.Visible
            : Visibility.Collapsed;

        _translationTextBlock.Text = translationText;
        _translationTextBlock.Visibility = showTranslation && !string.IsNullOrWhiteSpace(translationText)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public void ApplyStyle(SubtitleStyleSettings style)
    {
        _sourceTextBlock.FontSize = style.SourceFontSize;
        _sourceTextBlock.LineHeight = Math.Max(style.SourceFontSize * 1.3, style.SourceFontSize + 4);
        _sourceTextBlock.Foreground = new SolidColorBrush(ParseHexColor(style.SourceForegroundHex, ColorHelper.FromArgb(255, 241, 246, 251)));

        _translationTextBlock.FontSize = style.TranslationFontSize;
        _translationTextBlock.LineHeight = Math.Max(style.TranslationFontSize * 1.25, style.TranslationFontSize + 4);
        _translationTextBlock.Foreground = new SolidColorBrush(ParseHexColor(style.TranslationForegroundHex, Colors.White));

        _stackPanel.Spacing = style.DualSpacing;
        var overlayAlpha = (byte)Math.Clamp(Math.Round(style.BackgroundOpacity * 255), 0, 255);
        _rootBorder.Background = new SolidColorBrush(ColorHelper.FromArgb(overlayAlpha, 18, 23, 32));
        var verticalPadding = Math.Max(8, Math.Round(Math.Min(style.TranslationFontSize, 36) / 2));
        _rootBorder.Padding = new Thickness(18, verticalPadding, 18, verticalPadding + 2);
    }

    public void ShowOverlay(RectInt32 stageBounds, int bottomOffset)
    {
        EnsureWindow();
        PositionOverlay(stageBounds, bottomOffset);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        IsOverlayVisible = true;
    }

    public void HideOverlay()
    {
        if (!_isInitialized)
        {
            return;
        }

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        IsOverlayVisible = false;
    }

    public void PositionOverlay(RectInt32 stageBounds, int bottomOffset)
    {
        EnsureWindow();

        var scale = GetRasterizationScale();
        var availableWidth = Math.Max(stageBounds.Width / scale, 1);
        var overlayWidth = Math.Max(Math.Min(availableWidth - 24, _rootBorder.MaxWidth), 120);
        const int horizontalWindowPadding = 20;
        const int verticalWindowPadding = 24;
        var windowWidth = overlayWidth + horizontalWindowPadding;
        _rootBorder.Width = overlayWidth;
        _rootBorder.Measure(new Windows.Foundation.Size(overlayWidth, double.PositiveInfinity));
        var windowHeight = Math.Max(_rootBorder.DesiredSize.Height + verticalWindowPadding, 72);

        var rect = new RectInt32(
            stageBounds.X + Math.Max((int)Math.Round((stageBounds.Width - (windowWidth * scale)) / 2), 0),
            stageBounds.Y + Math.Max(stageBounds.Height - (int)Math.Round(windowHeight * scale) - bottomOffset, 0),
            (int)Math.Round(windowWidth * scale),
            (int)Math.Round(windowHeight * scale));

        _appWindow!.MoveAndResize(rect);
        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOP,
            rect.X,
            rect.Y,
            rect.Width,
            rect.Height,
            NativeMethods.SWP_NOACTIVATE);
    }

    public void CloseOverlay()
    {
        if (_isInitialized)
        {
            Close();
            _isInitialized = false;
            IsOverlayVisible = false;
        }
    }

    private void EnsureWindow()
    {
        if (_isInitialized)
        {
            return;
        }

        Activate();
        _hwnd = WindowNative.GetWindowHandle(this);
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, _ownerHwnd);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (_appWindow.Presenter is OverlappedPresenter overlappedPresenter)
        {
            overlappedPresenter.SetBorderAndTitleBar(false, false);
            overlappedPresenter.IsResizable = false;
            overlappedPresenter.IsMaximizable = false;
            overlappedPresenter.IsMinimizable = false;
        }

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        _isInitialized = true;
    }

    private double GetRasterizationScale()
    {
        return (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1d;
    }

    private static Color ParseHexColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        var value = hex.Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
        {
            value = value[1..];
        }

        try
        {
            return value.Length switch
            {
                6 => ColorHelper.FromArgb(
                    255,
                    Convert.ToByte(value[..2], 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16)),
                8 => ColorHelper.FromArgb(
                    Convert.ToByte(value[..2], 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16),
                    Convert.ToByte(value.Substring(6, 2), 16)),
                _ => fallback
            };
        }
        catch
        {
            return fallback;
        }
    }

    private static class NativeMethods
    {
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public const int GWLP_HWNDPARENT = -8;
        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }
}
