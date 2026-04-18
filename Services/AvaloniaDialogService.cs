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
    /// <summary>
    /// Shows a modal warmup notice dialog to the user on the UI thread and reports whether the user chose "Don't show again".
    /// </summary>
    /// <param name="cancellationToken">Accepted for API compatibility; not observed by this method.</param>
    /// <returns>`true` if the user selected "Don't show again"; `false` otherwise (including when the application is not available or the dialog was dismissed).</returns>
    /// <remarks>
    /// - Guard: If <c>Application.Current</c> is <c>null</c>, the method returns <c>false</c> immediately and does not attempt to dispatch to the UI thread.
    /// - Threading: Ensures the dialog is created and shown on the UI thread; if already on the UI thread it calls the UI routine directly, otherwise it invokes the UI routine via the UI dispatcher.
    /// - Persistence: The returned value indicates the user's choice but this method does not persist that choice to any storage or session state.
    /// - Cancellation: The provided <paramref name="cancellationToken"/> is not used; callers should not expect cancellation to abort the dialog display.
    /// <summary>
    /// Displays a modal warmup notice dialog on the Avalonia UI thread and returns whether the user selected the "Don't show again" option.
    /// </summary>
    /// <remarks>
    /// Expects the Avalonia application to be running; if <c>Application.Current</c> is <c>null</c> the method returns <c>false</c> immediately and does not attempt UI dispatch. If called off the UI thread, the dialog is invoked on the UI thread before being shown. The method does not persist the user's choice; it only reports the selection. Cancellation is accepted for API compatibility but is not observed by this method.
    /// </remarks>
    /// <param name="cancellationToken">Provided for API compatibility; this method does not observe or use the token.</param>
    /// <returns><c>true</c> if the user selected "Don't show again", <c>false</c> otherwise.</returns>
    public async Task<bool> ShowWarmupNoticeAsync(CancellationToken cancellationToken = default)
    {
        if (Application.Current is null)
            return false;

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
