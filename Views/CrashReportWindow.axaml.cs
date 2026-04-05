using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Babel.Player.Views;

/// <summary>
/// A standalone crash-report dialog that shows the full exception text,
/// the path to the log file, and buttons to copy or open the log folder.
/// Safe to show from any thread via <see cref="ShowOnUiThread"/>.
/// </summary>
public partial class CrashReportWindow : Window
{
    private string? _logFilePath;

    public CrashReportWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Populates the window with error details before it is shown.
    /// </summary>
    public void Configure(string errorText, string? logFilePath)
    {
        var errorTextBox = this.FindControl<TextBox>("ErrorTextBox");
        if (errorTextBox is not null) errorTextBox.Text = errorText;

        _logFilePath = logFilePath;

        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            var logPathText = this.FindControl<TextBlock>("LogPathText");
            if (logPathText is not null)
            {
                logPathText.Text = $"Log file: {logFilePath}";
                logPathText.IsVisible = true;
            }

            var openLogFolderButton = this.FindControl<Button>("OpenLogFolderButton");
            if (openLogFolderButton is not null) openLogFolderButton.IsVisible = true;
        }
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                var errorTextBox = this.FindControl<TextBox>("ErrorTextBox");
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(
                    clipboard,
                    errorTextBox?.Text ?? string.Empty);
            }

            var copyButton = this.FindControl<Button>("CopyButton");
            if (copyButton is not null) copyButton.Content = "Copied!";
        }
        catch
        {
            // Clipboard may be unavailable in some environments — fail silently.
        }
    }

    private void OnOpenLogFolderClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_logFilePath)) return;
        try
        {
            var folder = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch
        {
            // Explorer/Finder unavailable — ignore.
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // -------------------------------------------------------------------------
    // Static factory — marshal onto UI thread and show the window.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates and shows a <see cref="CrashReportWindow"/> on the Avalonia UI
    /// thread. Safe to call from background threads or exception handlers.
    /// </summary>
    public static void ShowOnUiThread(string errorText, string? logFilePath = null)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var window = new CrashReportWindow();
                window.Configure(errorText, logFilePath);
                window.Show();
            }
            catch
            {
                // If we cannot even show the error window, there is nothing
                // more we can safely do — the AppLog already has the entry.
            }
        }, Avalonia.Threading.DispatcherPriority.MaxValue);
    }
}
