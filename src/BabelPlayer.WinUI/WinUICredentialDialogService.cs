using BabelPlayer.App;
using BabelPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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

    public async Task<ShortcutProfile?> EditShortcutsAsync(ShortcutProfile currentProfile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentProfile);

        var shortcutService = new ShortcutService();
        var inputs = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var rows = new StackPanel
        {
            Spacing = 12
        };

        foreach (var action in ShortcutService.SupportedActions)
        {
            currentProfile.Bindings.TryGetValue(action.CommandId, out var currentBinding);
            var textBox = new TextBox
            {
                Text = currentBinding ?? string.Empty,
                PlaceholderText = "Leave blank to disable"
            };
            inputs[action.CommandId] = textBox;

            rows.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = action.DisplayName,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    },
                    new TextBlock
                    {
                        Text = action.Description,
                        Opacity = 0.72,
                        TextWrapping = TextWrapping.Wrap
                    },
                    textBox
                }
            });
        }

        var content = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "Use gestures like Ctrl+Shift+P or F11. Shortcut names stay compatible with the legacy WPF profile.",
                    TextWrapping = TextWrapping.Wrap
                },
                errorText,
                new ScrollViewer
                {
                    Height = 480,
                    Content = rows
                }
            }
        };

        ShortcutProfile? editedProfile = null;
        var dialog = new ContentDialog
        {
            XamlRoot = _dialogHost.XamlRoot,
            Title = "Keyboard Shortcuts",
            Content = content,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Reset Defaults",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var action in ShortcutService.SupportedActions)
                {
                    var value = inputs[action.CommandId].Text?.Trim();
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    bindings[action.CommandId] = shortcutService.Normalize(value);
                }

                var profile = new ShortcutProfile
                {
                    Bindings = bindings
                };

                var conflicts = shortcutService.FindConflicts(profile);
                if (conflicts.Count > 0)
                {
                    var firstConflict = conflicts[0];
                    errorText.Text = $"Conflict: {GetActionLabel(firstConflict.ExistingAction)} and {GetActionLabel(firstConflict.ConflictingAction)} both use {firstConflict.Gesture}.";
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }

                editedProfile = profile;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                errorText.Text = ex.Message;
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        cancellationToken.ThrowIfCancellationRequested();
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            return editedProfile;
        }

        if (result == ContentDialogResult.Secondary)
        {
            return ShortcutProfile.CreateDefault();
        }

        return null;
    }

    private static string GetActionLabel(string commandId)
    {
        foreach (var action in ShortcutService.SupportedActions)
        {
            if (string.Equals(action.CommandId, commandId, StringComparison.OrdinalIgnoreCase))
            {
                return action.DisplayName;
            }
        }

        return commandId;
    }
}
