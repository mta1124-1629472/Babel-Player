using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Babel.Player.Services;

/// <summary>
/// Shows a standalone, scrollable error window on the Avalonia UI thread.
/// The popup is non-blocking from the caller's perspective (fire-and-forget after
/// marshalling to the UI thread) but the dialog itself is application-modal.
/// </summary>
public sealed class AvaloniaErrorDialogService : IErrorDialogService
{
    private readonly AppLog _log;

    public AvaloniaErrorDialogService(AppLog log)
    {
        _log = log;
    }

    public Task ShowErrorAsync(string title, string fullDetail, string? logFilePath = null)
    {
        // Always marshal to the UI thread — callers may be on background threads.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return Dispatcher.UIThread.InvokeAsync(() =>
                ShowErrorAsync(title, fullDetail, logFilePath)).GetTask();
        }

        ShowDialog(title, fullDetail, logFilePath);
        return Task.CompletedTask;
    }

    private void ShowDialog(string title, string fullDetail, string? logFilePath)
    {
        // ── Layout ────────────────────────────────────────────────────────────
        var errorBox = new TextBox
        {
            Text              = fullDetail,
            IsReadOnly        = true,
            AcceptsReturn     = true,
            TextWrapping      = TextWrapping.Wrap,
            FontFamily        = new FontFamily("Cascadia Code,Consolas,Courier New,monospace"),
            FontSize          = 12,
            Background        = Brushes.Transparent,
            BorderThickness   = new Thickness(0),
            [ScrollViewer.HorizontalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var scroll = new ScrollViewer
        {
            Content           = errorBox,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight         = 400,
        };

        // Log-file path label (shown only when provided)
        Control? logPathRow = null;
        if (!string.IsNullOrEmpty(logFilePath))
        {
            logPathRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 8, 0, 0),
                Children =
                {
                    new TextBlock
                    {
                        Text = "Log file:",
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brushes.Gray,
                        FontSize = 12,
                    },
                    new TextBox
                    {
                        Text = logFilePath,
                        IsReadOnly = true,
                        FontSize = 12,
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
        }

        Window? dialog = null;

        var copyBtn = new Button { Content = "Copy Error Text", Margin = new Thickness(0, 0, 8, 0) };
        copyBtn.Click += (_, _) =>
        {
            if (dialog?.Clipboard is { } clipboard)
                _ = clipboard.SetTextAsync(fullDetail);
        };

        var openLogBtn = new Button { Content = "Open Log Folder", Margin = new Thickness(0, 0, 8, 0) };
        openLogBtn.IsVisible = !string.IsNullOrEmpty(logFilePath);
        openLogBtn.Click += (_, _) =>
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(logFilePath!);
                if (dir != null)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true,
                    });
            }
            catch (Exception ex)
            {
                _log.Error("Failed to open log folder.", ex);
            }
        };

        var closeBtn = new Button { Content = "Close" };
        closeBtn.Click += (_, _) => dialog?.Close();

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { copyBtn, openLogBtn, closeBtn },
        };

        var headerText = new TextBlock
        {
            Text       = title,
            FontSize   = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.OrangeRed,
            Margin     = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        };

        var divider = new Border
        {
            Height = 1,
            Background = Brushes.LightGray,
            Margin = new Thickness(0, 0, 0, 10),
        };

        var root = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                headerText,
                divider,
                scroll,
            },
        };

        if (logPathRow != null) root.Children.Add(logPathRow);
        root.Children.Add(buttonRow);

        dialog = new Window
        {
            Title          = "Error Details",
            Width          = 720,
            MinWidth       = 400,
            MinHeight      = 260,
            SizeToContent  = SizeToContent.Height,
            Content        = root,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize      = true,
        };

        // Try to attach to the main window as owner so it centres correctly.
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt &&
            dt.MainWindow is { } main)
        {
            dialog.ShowDialog(main);
        }
        else
        {
            dialog.Show();
        }
    }
}
