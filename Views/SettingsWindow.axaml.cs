using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Babel.Player.ViewModels;

namespace Babel.Player.Views;

public partial class SettingsWindow : Window
{
    private EventHandler<PixelPointEventArgs>? _windowPositionChangedHandler;
    private EventHandler? _windowScalingChangedHandler;
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

        _windowPositionChangedHandler ??= OnWindowPositionChanged;
        _windowScalingChangedHandler ??= OnWindowMetricsChanged;
        _screensChangedHandler ??= OnScreensChanged;

        PositionChanged += _windowPositionChangedHandler;
        ScalingChanged += _windowScalingChangedHandler;

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
        if (_windowPositionChangedHandler is not null)
            PositionChanged -= _windowPositionChangedHandler;

        if (_windowScalingChangedHandler is not null)
            ScalingChanged -= _windowScalingChangedHandler;

        if (_subscribedScreens is not null && _screensChangedHandler is not null)
            _subscribedScreens.Changed -= _screensChangedHandler;

        _windowPositionChangedHandler = null;
        _windowScalingChangedHandler = null;
        _screensChangedHandler = null;
        _subscribedScreens = null;

        if (DataContext is System.IDisposable disposable)
            disposable.Dispose();
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e) =>
        RefreshHdrDisplayState();

    private void OnWindowMetricsChanged(object? sender, System.EventArgs e) =>
        RefreshHdrDisplayState();

    private void OnScreensChanged(object? sender, System.EventArgs e) =>
        RefreshHdrDisplayState();

    private void RefreshHdrDisplayState()
    {
        if (DataContext is SettingsViewModel vm)
            vm.RefreshHdrDisplayState();
    }
}
