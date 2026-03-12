using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace BabelPlayer.WinUI;

public interface IFullscreenOverlayWindow
{
    event Action? ActivityDetected;
    event Action<bool>? InteractionStateChanged;

    Button PlayPauseButton { get; }
    Button SubtitleToggleButton { get; }
    DropDownButton SubtitleModeButton { get; }
    DropDownButton SubtitleStyleButton { get; }
    Button PipButton { get; }
    Button ImmersiveButton { get; }
    DropDownButton SettingsButton { get; }
    Button ExitFullscreenButton { get; }
    Slider PositionSlider { get; }
    TextBlock CurrentTimeTextBlock { get; }
    TextBlock DurationTextBlock { get; }
    bool IsOverlayVisible { get; }

    void ShowOverlay(RectInt32 displayBounds);
    void HideOverlay();
    void PositionOverlay(RectInt32 displayBounds);
    void CloseOverlay();
}

public sealed class FullscreenOverlayWindow : Window, IFullscreenOverlayWindow
{
    private readonly Border _rootBorder;
    private readonly IntPtr _ownerHwnd;
    private AppWindow? _appWindow;
    private IntPtr _hwnd;
    private bool _isInitialized;
    private bool _isPointerPressed;

    public FullscreenOverlayWindow(IntPtr ownerHwnd)
    {
        _ownerHwnd = ownerHwnd;
        var overlayHost = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(12, 10, 12, 10)
        };
        overlayHost.PointerMoved += OverlayHost_PointerActivity;
        overlayHost.PointerPressed += OverlayHost_PointerActivity;
        overlayHost.PointerPressed += OverlayHost_PointerPressed;
        overlayHost.PointerReleased += OverlayHost_PointerReleased;
        overlayHost.PointerCaptureLost += OverlayHost_PointerCaptureLost;
        overlayHost.PointerEntered += OverlayHost_PointerEntered;
        overlayHost.PointerExited += OverlayHost_PointerExited;

        _rootBorder = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(16, 10, 16, 10),
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(ColorHelper.FromArgb(220, 12, 18, 28)),
            MaxWidth = 1120
        };

        var overlayRoot = new StackPanel
        {
            Spacing = 10
        };

        var controlsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8
        };

        PlayPauseButton = CreateIconButton(Symbol.Play, "Play or pause");
        controlsRow.Children.Add(PlayPauseButton);

        SubtitleToggleButton = new Button
        {
            Content = "Subtitles",
            MinWidth = 92
        };
        controlsRow.Children.Add(SubtitleToggleButton);

        SubtitleModeButton = CreateDropDownButton("Mode", "Subtitle mode");
        controlsRow.Children.Add(SubtitleModeButton);

        SubtitleStyleButton = CreateDropDownButton("Style", "Subtitle style");
        controlsRow.Children.Add(SubtitleStyleButton);

        PipButton = CreateIconButton(Symbol.SwitchApps, "Picture in picture");
        controlsRow.Children.Add(PipButton);

        ImmersiveButton = CreateIconButton(Symbol.HideBcc, "Immersive mode");
        controlsRow.Children.Add(ImmersiveButton);

        SettingsButton = CreateDropDownButton(new SymbolIcon(Symbol.Setting), "Playback settings");
        controlsRow.Children.Add(SettingsButton);

        ExitFullscreenButton = CreateIconButton(Symbol.FullScreen, "Exit fullscreen");
        controlsRow.Children.Add(ExitFullscreenButton);

        overlayRoot.Children.Add(controlsRow);

        var scrubberGrid = new Grid
        {
            ColumnSpacing = 12
        };
        scrubberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scrubberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scrubberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        CurrentTimeTextBlock = new TextBlock
        {
            Text = "00:00",
            VerticalAlignment = VerticalAlignment.Center
        };
        scrubberGrid.Children.Add(CurrentTimeTextBlock);

        PositionSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            MinWidth = 420
        };
        PositionSlider.PointerMoved += OverlayHost_PointerActivity;
        PositionSlider.PointerPressed += OverlayHost_PointerActivity;
        PositionSlider.PointerPressed += OverlayHost_PointerPressed;
        PositionSlider.PointerReleased += OverlayHost_PointerReleased;
        PositionSlider.PointerCaptureLost += OverlayHost_PointerCaptureLost;
        PositionSlider.PointerEntered += OverlayHost_PointerEntered;
        PositionSlider.PointerExited += OverlayHost_PointerExited;
        Grid.SetColumn(PositionSlider, 1);
        scrubberGrid.Children.Add(PositionSlider);

        DurationTextBlock = new TextBlock
        {
            Text = "00:00",
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(DurationTextBlock, 2);
        scrubberGrid.Children.Add(DurationTextBlock);

        overlayRoot.Children.Add(scrubberGrid);
        _rootBorder.Child = overlayRoot;
        overlayHost.Children.Add(_rootBorder);
        Content = overlayHost;
    }

    public event Action? ActivityDetected;
    public event Action<bool>? InteractionStateChanged;

    public Button PlayPauseButton { get; }
    public Button SubtitleToggleButton { get; }
    public DropDownButton SubtitleModeButton { get; }
    public DropDownButton SubtitleStyleButton { get; }
    public Button PipButton { get; }
    public Button ImmersiveButton { get; }
    public DropDownButton SettingsButton { get; }
    public Button ExitFullscreenButton { get; }
    public Slider PositionSlider { get; }
    public TextBlock CurrentTimeTextBlock { get; }
    public TextBlock DurationTextBlock { get; }
    public bool IsOverlayVisible { get; private set; }

    public void ShowOverlay(RectInt32 displayBounds)
    {
        EnsureWindow();
        PositionOverlay(displayBounds);
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

    public void PositionOverlay(RectInt32 displayBounds)
    {
        EnsureWindow();

        var scale = GetRasterizationScale();
        var availableWidth = Math.Max(displayBounds.Width / scale, 320);
        var overlayWidth = Math.Min(Math.Max(availableWidth - 48, 320), _rootBorder.MaxWidth);
        const int horizontalWindowPadding = 32;
        const int verticalWindowPadding = 20;
        _rootBorder.Width = overlayWidth;
        _rootBorder.Measure(new Windows.Foundation.Size(overlayWidth, double.PositiveInfinity));
        var overlayHeight = Math.Max(_rootBorder.DesiredSize.Height, 88);

        var windowWidth = (int)Math.Round((overlayWidth + horizontalWindowPadding) * scale);
        var windowHeight = (int)Math.Round((overlayHeight + verticalWindowPadding) * scale);
        var rect = new RectInt32(
            displayBounds.X + Math.Max((displayBounds.Width - windowWidth) / 2, 0),
            displayBounds.Y + Math.Max(displayBounds.Height - windowHeight - 24, 0),
            windowWidth,
            windowHeight);

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

    private void OverlayHost_PointerActivity(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ActivityDetected?.Invoke();
    }

    private static Button CreateIconButton(Symbol symbol, string automationName)
    {
        var button = new Button
        {
            Content = new SymbolIcon(symbol),
            MinWidth = 44,
            Padding = new Thickness(12, 8, 12, 8)
        };
        AutomationProperties.SetName(button, automationName);
        ToolTipService.SetToolTip(button, automationName);
        return button;
    }

    private static DropDownButton CreateDropDownButton(string label, string automationName)
    {
        var button = new DropDownButton
        {
            Content = label,
            MinWidth = 88
        };
        AutomationProperties.SetName(button, automationName);
        ToolTipService.SetToolTip(button, automationName);
        return button;
    }

    private static DropDownButton CreateDropDownButton(IconElement icon, string automationName)
    {
        var button = new DropDownButton
        {
            Content = icon,
            MinWidth = 44
        };
        AutomationProperties.SetName(button, automationName);
        ToolTipService.SetToolTip(button, automationName);
        return button;
    }

    private void OverlayHost_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ActivityDetected?.Invoke();
        InteractionStateChanged?.Invoke(true);
    }

    private void OverlayHost_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPointerPressed)
        {
            InteractionStateChanged?.Invoke(false);
        }
    }

    private void OverlayHost_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPointerPressed = true;
        ActivityDetected?.Invoke();
        InteractionStateChanged?.Invoke(true);
    }

    private void OverlayHost_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPointerPressed = false;
        InteractionStateChanged?.Invoke(false);
    }

    private void OverlayHost_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPointerPressed = false;
        InteractionStateChanged?.Invoke(false);
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
