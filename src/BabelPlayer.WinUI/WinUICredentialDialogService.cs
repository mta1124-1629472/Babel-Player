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

    public Task<ShortcutProfile?> EditShortcutsAsync(ShortcutProfile currentProfile, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ShortcutProfile?>(currentProfile);
    }
}
