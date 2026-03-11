using Microsoft.UI.Xaml;

namespace BabelPlayer.WinUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(new ShellCompositionRoot());
        _window.Activate();
    }
}
