using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Babel.Player.ViewModels;

namespace Babel.Player.Views;

public partial class SettingsWindow : Window
{
    private EventHandler? _screensChangedHandler;
    private Screens? _subscribedScreens;

    public SettingsWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);

        _screensChangedHandler ??= OnScreensChanged;

        _subscribedScreens = Screens;
        _subscribedScreens.Changed += _screensChangedHandler;

        RefreshHdrDisplayState();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.CancelCommand.Execute(null);
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.ApplyCommand.Execute(null);
    }

    private void OnOKClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.OKCommand.Execute(null);
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        if (_subscribedScreens is not null && _screensChangedHandler is not null)
            _subscribedScreens.Changed -= _screensChangedHandler;

        _screensChangedHandler = null;
        _subscribedScreens = null;

        if (DataContext is System.IDisposable disposable)
            disposable.Dispose();
    }

    private void OnScreensChanged(object? sender, System.EventArgs e) =>
        RefreshHdrDisplayState();

    private void RefreshHdrDisplayState()
    {
        if (DataContext is SettingsViewModel vm)
            vm.RefreshHdrDisplayState();
    }
}
