using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Babel.Player.Models;
using Babel.Player.ViewModels;

namespace Babel.Player.Views;

public partial class SpeakerReferenceWizardWindow : Window
{
    private static readonly string[] AudioExtensions = ["*.wav", "*.mp3", "*.flac", "*.ogg", "*.m4a"];
    private static readonly string[] AllFilesPattern = ["*.*"];

    public SpeakerReferenceWizardWindow()
    {
        InitializeComponent();

        if (this.FindControl<MpvVideoView>("WizardVideoView") is { } wizardVideo)
        {
            wizardVideo.HandleReady += OnWizardVideoHandleReady;
            wizardVideo.SizeChanged += (_, _) => UpdateWizardVideoViewport();
        }

        PositionChanged += (_, _) => UpdateWizardVideoViewport();
    }

    public void OnMinimizeWindowClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    public void OnToggleMaximizeWindowClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    public void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is Visual sourceVisual && sourceVisual.FindAncestorOfType<Button>() is not null)
            return;

        if (e.ClickCount == 2)
        {
            OnToggleMaximizeWindowClick(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    private void SyncChromeWindowState()
    {
        if (this.FindControl<Control>("ChromeMaximizeIcon") is not { } maxIcon ||
            this.FindControl<Control>("ChromeRestoreIcon") is not { } restoreIcon)
            return;

        var maximized = WindowState == WindowState.Maximized;
        maxIcon.IsVisible = !maximized;
        restoreIcon.IsVisible = maximized;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            SyncChromeWindowState();
    }

    private void OnWizardVideoHandleReady(object? sender, IntPtr hwnd)
    {
        if (DataContext is SpeakerReferenceWizardViewModel vm)
        {
            vm.MiniPreview.AttachAndLoad(hwnd);
            UpdateWizardVideoViewport();
        }
    }

    private void UpdateWizardVideoViewport()
    {
        if (this.FindControl<MpvVideoView>("WizardVideoView") is not { } videoView ||
            DataContext is not SpeakerReferenceWizardViewModel vm)
        {
            return;
        }

        vm.MiniPreview.SetViewport(videoView, this);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();

        base.OnClosed(e);
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SyncChromeWindowState();
        if (DataContext is SpeakerReferenceWizardViewModel vm)
            await vm.LoadAsync();
    }

    private async void OnBrowseFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SpeakerReferenceWizardViewModel vm ||
            sender is not Button { Tag: SpeakerReferenceDraftItem item })
        {
            return;
        }

        if (!item.ReferenceActionsEnabled)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Select reference clip for {item.SpeakerId}",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio Files") { Patterns = AudioExtensions },
                new FilePickerFileType("All Files") { Patterns = AllFilesPattern },
            ],
        });

        if (files.Count == 0)
            return;

        var selectedPath = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(selectedPath))
            return;

        await vm.ApplyBrowseSelectionAsync(item, selectedPath);
    }

    private void OnKeepAutoClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SpeakerReferenceWizardViewModel vm ||
            sender is not Button { Tag: SpeakerReferenceDraftItem item })
        {
            return;
        }

        vm.KeepAutoCommand.Execute(item);
    }

    private async void OnUseSelectedSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SpeakerReferenceWizardViewModel vm ||
            sender is not Button { Tag: SpeakerReferenceDraftItem item })
        {
            return;
        }

        if (!item.ReferenceActionsEnabled)
            return;

        var firstAttempt = await vm.UseSelectedSegmentAsync(item, allowSpeakerMismatch: false);
        if (firstAttempt.Status != UseSelectedSegmentStatus.RequiresSpeakerMismatchConfirmation)
            return;

        var confirmed = await ShowConfirmationDialogAsync(
            title: "Use segment with different speaker?",
            message: $"{firstAttempt.Message} Continue anyway?");
        if (!confirmed)
            return;

        await vm.UseSelectedSegmentAsync(item, allowSpeakerMismatch: true);
    }

    private async void OnAutoPickAnotherClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SpeakerReferenceWizardViewModel vm ||
            sender is not Button { Tag: SpeakerReferenceDraftItem item })
        {
            return;
        }

        if (!item.ReferenceActionsEnabled)
            return;

        await vm.AutoPickAnotherCommand.ExecuteAsync(item);
    }

    private async void OnUsePlayheadClipClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SpeakerReferenceWizardViewModel vm ||
            sender is not Button { Tag: SpeakerReferenceDraftItem item })
        {
            return;
        }

        if (!item.ReferenceActionsEnabled)
            return;

        await vm.UsePlayheadClipAsync(item);
    }

    private async void OnJumpToSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SpeakerReferenceWizardViewModel vm ||
            sender is not Button { Tag: WorkflowSegmentState segment })
        {
            return;
        }

        await vm.JumpToSegmentAsync(segment);
    }

    private async void OnPlayReferenceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SpeakerReferenceWizardViewModel vm ||
            sender is not Button { Tag: SpeakerReferenceDraftItem item })
        {
            return;
        }

        await vm.PlayReferencePreviewCommand.ExecuteAsync(item);
    }

    private void OnRevealReferenceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SpeakerReferenceDraftItem item })
            return;

        if (string.IsNullOrWhiteSpace(item.EffectiveReferencePath) || !File.Exists(item.EffectiveReferencePath))
        {
            item.SetInlineError("Reference file is missing on disk.");
            return;
        }

        item.SetInlineError(string.Empty);
        try
        {
            RevealFileInFileManager(item.EffectiveReferencePath);
        }
        catch (Exception ex)
        {
            item.SetInlineError($"Could not open folder: {ex.Message}");
        }
    }

    private static void RevealFileInFileManager(string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{fullPath}\"",
                UseShellExecute = true,
            });
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                Arguments = $"-R \"{fullPath}\"",
                UseShellExecute = false,
            });
            return;
        }

        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Process.Start(new ProcessStartInfo { FileName = "xdg-open", Arguments = dir, UseShellExecute = true });
    }

    private void OnUseActiveTtsVoiceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SpeakerReferenceWizardViewModel vm ||
            sender is not Button { Tag: SpeakerReferenceDraftItem item })
        {
            return;
        }

        vm.UseActiveTtsVoiceForSpeaker(item);
    }

    private void OnClearVoiceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SpeakerReferenceWizardViewModel vm ||
            sender is not Button { Tag: SpeakerReferenceDraftItem item })
        {
            return;
        }

        vm.ClearDraftVoiceForSpeaker(item);
    }

    private async void OnFinishClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SpeakerReferenceWizardViewModel vm)
        {
            Close(false);
            return;
        }

        await vm.FinishAsync();
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SpeakerReferenceWizardViewModel vm)
            vm.Cancel();
        Close(false);
    }

    private async Task<bool> ShowConfirmationDialogAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = title,
        };

        var keepButton = new Button { Content = "Continue", Padding = new Thickness(12, 6) };
        var cancelButton = new Button { Content = "Cancel", Padding = new Thickness(12, 6) };
        keepButton.Click += (_, _) =>
        {
            tcs.TrySetResult(true);
            dialog.Close();
        };
        cancelButton.Click += (_, _) =>
        {
            tcs.TrySetResult(false);
            dialog.Close();
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, keepButton },
                },
            },
        };

        _ = dialog.ShowDialog(this);
        return await tcs.Task;
    }
}
