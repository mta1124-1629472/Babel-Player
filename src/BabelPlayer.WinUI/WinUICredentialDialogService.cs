using BabelPlayer.App;
using BabelPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BabelPlayer.WinUI;

public sealed class WinUICredentialDialogService : ICredentialDialogService
{
    private readonly FrameworkElement _dialogHost;

    public WinUICredentialDialogService(FrameworkElement dialogHost)
    {
        _dialogHost = dialogHost;
    }

    public async Task<string?> PromptForApiKeyAsync(string title, string message, string submitButtonText, CancellationToken cancellationToken = default)
    {
        var textBox = new TextBox
        {
            PlaceholderText = "Paste API key"
        };

        var dialog = new ContentDialog
        {
            XamlRoot = _dialogHost.XamlRoot,
            Title = title,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    textBox
                }
            },
            PrimaryButtonText = submitButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        cancellationToken.ThrowIfCancellationRequested();
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(textBox.Text)
            ? textBox.Text.Trim()
            : null;
    }

    public async Task<(string ApiKey, string Region)?> PromptForApiKeyWithRegionAsync(string title, string message, string submitButtonText, CancellationToken cancellationToken = default)
    {
        var apiKeyBox = new TextBox
        {
            PlaceholderText = "API key"
        };

        var regionBox = new TextBox
        {
            PlaceholderText = "Region"
        };

        var dialog = new ContentDialog
        {
            XamlRoot = _dialogHost.XamlRoot,
            Title = title,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    apiKeyBox,
                    regionBox
                }
            },
            PrimaryButtonText = submitButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        cancellationToken.ThrowIfCancellationRequested();
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(apiKeyBox.Text) || string.IsNullOrWhiteSpace(regionBox.Text))
        {
            return null;
        }

        return (apiKeyBox.Text.Trim(), regionBox.Text.Trim());
    }

    public async Task<LlamaCppBootstrapChoice> PromptForLlamaCppBootstrapChoiceAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        var choice = LlamaCppBootstrapChoice.Cancel;
        var dialog = new ContentDialog
        {
            XamlRoot = _dialogHost.XamlRoot,
            Title = title,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Automatic install is recommended for the local HY-MT models.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap }
                }
            },
            PrimaryButtonText = "Install Automatically",
            SecondaryButtonText = "Choose Existing",
            CloseButtonText = "More Options"
        };

        cancellationToken.ThrowIfCancellationRequested();
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            return LlamaCppBootstrapChoice.InstallAutomatically;
        }

        if (result == ContentDialogResult.Secondary)
        {
            return LlamaCppBootstrapChoice.ChooseExisting;
        }

        var followUp = new ContentDialog
        {
            XamlRoot = _dialogHost.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = "Open the official llama.cpp download page, or cancel and keep the current selection unchanged.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Open Download Page",
            CloseButtonText = "Cancel"
        };

        result = await followUp.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            choice = LlamaCppBootstrapChoice.OpenOfficialDownloadPage;
        }

        return choice;
    }

    public Task<ShortcutProfile?> EditShortcutsAsync(ShortcutProfile currentProfile, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ShortcutProfile?>(currentProfile);
    }
}
