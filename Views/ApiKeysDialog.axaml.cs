using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Babel.Player.Views;

public partial class ApiKeysDialog : Window
{
    public ApiKeysDialog()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
