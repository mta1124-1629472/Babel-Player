using BabelPlayer.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BabelPlayer.WinUI;

public sealed class WinUICredentialDialogService : ICredentialDialogService
{
    private readonly FrameworkElement _dialogHost;
    private readonly IShortcutProfileService _shortcutProfileService;
    private readonly Func<IDisposable>? _modalSuppressionFactory;

    public WinUICredentialDialogService(
        FrameworkElement dialogHost,
        IShortcutProfileService shortcutProfileService,
        Func<IDisposable>? modalSuppressionFactory = null)
    {
        _dialogHost = dialogHost;
        _shortcutProfileService = shortcutProfileService;
        _modalSuppressionFactory = modalSuppressionFactory;
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
        var result = await ShowDialogAsync(dialog);
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
        var result = await ShowDialogAsync(dialog);
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(apiKeyBox.Text) || string.IsNullOrWhiteSpace(regionBox.Text))
        {
            return null;
        }

        return (apiKeyBox.Text.Trim(), regionBox.Text.Trim());
    }

    public async Task<LlamaCppBootstrapChoice> PromptForLlamaCppBootstrapChoiceAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new LlamaCppBootstrapDialog(_dialogHost.XamlRoot, title, message);
        var result = await ShowDialogAsync(dialog);
        return result == ContentDialogResult.None ? dialog.Choice : LlamaCppBootstrapChoice.Cancel;
    }

    public async Task<ShortcutProfile?> EditShortcutsAsync(ShortcutProfile currentProfile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentProfile);

        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new ShortcutEditorDialog(_dialogHost.XamlRoot, _shortcutProfileService, currentProfile);
        var result = await ShowDialogAsync(dialog);
        return result == ContentDialogResult.Primary ? dialog.ResultProfile : null;
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        using var suppression = _modalSuppressionFactory?.Invoke();
        return await dialog.ShowAsync();
    }
}
