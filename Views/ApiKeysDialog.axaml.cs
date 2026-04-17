using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Babel.Player.Views;

public partial class ApiKeysDialog : Window
{
    public ApiKeysDialog()
    {
        InitializeComponent();
    }

    public void OnMinimizeWindowClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    public void OnToggleMaximizeWindowClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    public void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is Visual sourceVisual && sourceVisual.FindAncestorOfType<Button>() is not null)
            return;

        if (e.ClickCount == 2)
        {
            OnToggleMaximizeWindowClick(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void SyncChromeWindowState()
    {
        if (this.FindControl<Control>("ChromeMaximizeIcon") is not { } maxIcon ||
            this.FindControl<Control>("ChromeRestoreIcon") is not { } restoreIcon)
            return;

        var maximized = WindowState == WindowState.Maximized;
        maxIcon.IsVisible = !maximized;
        restoreIcon.IsVisible = maximized;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            SyncChromeWindowState();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SyncChromeWindowState();
    }
}
