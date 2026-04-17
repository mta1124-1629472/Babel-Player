using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Babel.Player.Services;

public sealed class AvaloniaDialogService : IDialogService
{
    public async Task<bool> ShowWarmupNoticeAsync(CancellationToken cancellationToken = default)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return await ShowWarmupNoticeUiAsync().ConfigureAwait(true);

        return await Dispatcher.UIThread.InvokeAsync(ShowWarmupNoticeUiAsync).ConfigureAwait(true);
    }

    private static async Task<bool> ShowWarmupNoticeUiAsync()
    {
        var owner = GetMainWindow();
        if (owner is null)
            return false;

        var persistDontShowAgain = false;
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
        };
        panel.Children.Add(new TextBlock
        {
            Text =
                "The local inference host may take 30 to 60 seconds to start. " +
                "Please wait for the status to show 'Ready' before running the pipeline.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
        });

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
        };
        var dontShowAgain = new Button
        {
            Content = "Don't show again",
            MinWidth = 140,
        };
        var ok = new Button
        {
            Content = "OK",
            MinWidth = 96,
            IsDefault = true,
        };
        buttonRow.Children.Add(dontShowAgain);
        buttonRow.Children.Add(ok);
        panel.Children.Add(buttonRow);

        var dialog = new Window
        {
            Title = "Local inference host",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        dontShowAgain.Click += (_, _) =>
        {
            persistDontShowAgain = true;
            dialog.Close();
        };
        ok.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return persistDontShowAgain;
    }

    private static Window? GetMainWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow as Window
            : null;
}
