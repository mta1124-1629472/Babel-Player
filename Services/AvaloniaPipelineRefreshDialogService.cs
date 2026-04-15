using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Babel.Player.Services;

public sealed class AvaloniaPipelineRefreshDialogService : IPipelineRefreshDialogService
{
    public async Task<PipelineRefreshScope?> PromptRefreshScopeAsync(PipelineRefreshSection section)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return await PromptRefreshScopeUiAsync(section).ConfigureAwait(true);

        return await Dispatcher.UIThread.InvokeAsync(() => PromptRefreshScopeUiAsync(section)).ConfigureAwait(true);
    }

    private static async Task<PipelineRefreshScope?> PromptRefreshScopeUiAsync(PipelineRefreshSection section)
    {
        var owner = GetMainWindow();
        if (owner is null)
            return null;

        PipelineRefreshScope? choice = null;

        var dialog = new Window
        {
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = section switch
            {
                PipelineRefreshSection.Transcription => "Re-run transcription",
                PipelineRefreshSection.Diarization => "Re-run diarization",
                PipelineRefreshSection.Translation => "Re-run translation",
                _ => "Re-run pipeline stage",
            },
        };

        var thisOnlyBtn = new Button { Content = "This stage only", Padding = new Thickness(12, 6) };
        var remainingBtn = new Button { Content = "This stage and downstream", Padding = new Thickness(12, 6) };
        var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(12, 6) };

        thisOnlyBtn.Click += (_, _) =>
        {
            choice = PipelineRefreshScope.ThisStageOnly;
            dialog.Close();
        };
        remainingBtn.Click += (_, _) =>
        {
            choice = PipelineRefreshScope.RemainingPipeline;
            dialog.Close();
        };
        cancelBtn.Click += (_, _) => dialog.Close();

        var (title, detail) = section switch
        {
            PipelineRefreshSection.Transcription => (
                "Re-run transcription?",
                "This stage only: transcribe again and stop.\n\nThis stage and downstream: transcribe, then continue with speaker mapping (if enabled), translation, and dub — same as advancing the full pipeline from media-loaded."),
            PipelineRefreshSection.Diarization => (
                "Re-run diarization?",
                "This stage only: run speaker mapping again and stop.\n\nThis stage and downstream: diarize, then re-run translation and dub."),
            PipelineRefreshSection.Translation => (
                "Re-run translation?",
                "This stage only: translate again from the current transcript and stop.\n\nThis stage and downstream: translate, then re-generate dub audio."),
            _ => ("Re-run stage?", string.Empty),
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = detail,
                    Opacity = 0.9,
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelBtn, thisOnlyBtn, remainingBtn },
                },
            },
        };

        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return choice;
    }

    public async Task<bool> ConfirmRegenerateDubAsync()
    {
        if (Dispatcher.UIThread.CheckAccess())
            return await ConfirmRegenerateDubUiAsync().ConfigureAwait(true);

        return await Dispatcher.UIThread.InvokeAsync(() => ConfirmRegenerateDubUiAsync()).ConfigureAwait(true);
    }

    private static async Task<bool> ConfirmRegenerateDubUiAsync()
    {
        var owner = GetMainWindow();
        if (owner is null)
            return false;

        var confirmed = false;

        var dialog = new Window
        {
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = "Re-generate dub",
        };

        var okBtn = new Button { Content = "Re-generate dub", Padding = new Thickness(12, 6) };
        var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(12, 6) };

        okBtn.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        cancelBtn.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Re-generate dub audio from the current translation? Existing dub output will be replaced.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelBtn, okBtn },
                },
            },
        };

        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return confirmed;
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow as Window;
        return null;
    }
}
