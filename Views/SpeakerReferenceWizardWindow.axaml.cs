using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
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
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
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
