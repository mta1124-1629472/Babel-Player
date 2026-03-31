using Avalonia.Controls;
using Avalonia.Interactivity;
using Babel.Player.ViewModels;

namespace Babel.Player.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
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
}