using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

internal sealed class ApiKeyPromptWindow : Window
{
    private readonly TextBox _apiKeyBox;

    public ApiKeyPromptWindow(string title, string message, string submitButtonText)
    {
        Title = title;
        Width = 460;
        Height = 220;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#151515"));

        _apiKeyBox = new TextBox
        {
            Watermark = "Paste API key"
        };

        var okButton = new Button
        {
            Content = submitButtonText,
            MinWidth = 96
        };
        okButton.Click += (_, _) =>
        {
            Result = string.IsNullOrWhiteSpace(_apiKeyBox.Text) ? null : _apiKeyBox.Text.Trim();
            Close();
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 96
        };
        cancelButton.Click += (_, _) =>
        {
            Result = null;
            Close();
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.White
                },
                _apiKeyBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children =
                    {
                        cancelButton,
                        okButton
                    }
                }
            }
        };
    }

    public string? Result { get; private set; }
}

internal sealed class ApiKeyWithRegionPromptWindow : Window
{
    private readonly TextBox _apiKeyBox;
    private readonly TextBox _regionBox;

    public ApiKeyWithRegionPromptWindow(string title, string message, string submitButtonText)
    {
        Title = title;
        Width = 460;
        Height = 270;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#151515"));

        _apiKeyBox = new TextBox
        {
            Watermark = "API key"
        };
        _regionBox = new TextBox
        {
            Watermark = "Region"
        };

        var okButton = new Button
        {
            Content = submitButtonText,
            MinWidth = 96
        };
        okButton.Click += (_, _) =>
        {
            Result = string.IsNullOrWhiteSpace(_apiKeyBox.Text) || string.IsNullOrWhiteSpace(_regionBox.Text)
                ? null
                : (_apiKeyBox.Text.Trim(), _regionBox.Text.Trim());
            Close();
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 96
        };
        cancelButton.Click += (_, _) =>
        {
            Result = null;
            Close();
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.White
                },
                _apiKeyBox,
                _regionBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children =
                    {
                        cancelButton,
                        okButton
                    }
                }
            }
        };
    }

    public (string ApiKey, string Region)? Result { get; private set; }
}

internal sealed class LlamaCppBootstrapPromptWindow : Window
{
    public LlamaCppBootstrapPromptWindow(string title, string message)
    {
        Title = title;
        Width = 520;
        Height = 260;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#151515"));

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.White
                },
                CreateChoiceButton("Install Automatically", LlamaCppBootstrapChoice.InstallAutomatically),
                CreateChoiceButton("Choose Existing", LlamaCppBootstrapChoice.ChooseExisting),
                CreateChoiceButton("Open Download Page", LlamaCppBootstrapChoice.OpenOfficialDownloadPage),
                new Button
                {
                    Content = "Cancel",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MinWidth = 96
                }
            }
        };

        if (Content is StackPanel panel && panel.Children[^1] is Button cancelButton)
        {
            cancelButton.Click += (_, _) =>
            {
                Choice = LlamaCppBootstrapChoice.Cancel;
                Close();
            };
        }
    }

    public LlamaCppBootstrapChoice Choice { get; private set; } = LlamaCppBootstrapChoice.Cancel;

    private Button CreateChoiceButton(string label, LlamaCppBootstrapChoice choice)
    {
        var button = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        button.Click += (_, _) =>
        {
            Choice = choice;
            Close();
        };
        return button;
    }
}
