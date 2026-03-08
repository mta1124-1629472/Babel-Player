using BabelPlayer.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BabelPlayer.UI;

internal sealed class ShortcutEditorWindow : Window
{
    private static readonly (string Id, string Label)[] ShortcutDefinitions =
    [
        ("play_pause", "Play / Pause"),
        ("seek_back_small", "Seek Back 5s"),
        ("seek_forward_small", "Seek Forward 5s"),
        ("seek_back_large", "Seek Back 15s"),
        ("seek_forward_large", "Seek Forward 15s"),
        ("previous_frame", "Previous Frame"),
        ("next_frame", "Next Frame"),
        ("speed_down", "Speed Down"),
        ("speed_up", "Speed Up"),
        ("speed_reset", "Speed Reset"),
        ("subtitle_toggle", "Toggle Subtitles"),
        ("translation_toggle", "Toggle Translation"),
        ("subtitle_delay_back", "Subtitle Delay -50 ms"),
        ("subtitle_delay_forward", "Subtitle Delay +50 ms"),
        ("audio_delay_back", "Audio Delay -50 ms"),
        ("audio_delay_forward", "Audio Delay +50 ms"),
        ("fullscreen", "Fullscreen"),
        ("pip", "Picture in Picture"),
        ("next_item", "Next Playlist Item"),
        ("previous_item", "Previous Playlist Item"),
        ("mute", "Mute")
    ];

    private readonly Dictionary<string, TextBox> _boxes = [];
    private readonly TextBlock _errorText;

    public ShortcutEditorWindow(ShortcutProfile currentProfile)
    {
        Title = "Babel Player Shortcuts";
        Width = 560;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x16));
        Foreground = Brushes.White;

        var root = new Grid
        {
            Margin = new Thickness(16)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = "Edit persisted keyboard shortcuts",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(header);

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scrollViewer, 1);

        var rows = new StackPanel();
        foreach (var definition in ShortcutDefinitions)
        {
            var row = new Grid
            {
                Margin = new Thickness(0, 0, 0, 10)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = definition.Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            var box = new TextBox
            {
                Text = currentProfile.Bindings.TryGetValue(definition.Id, out var value) ? value : string.Empty,
                Margin = new Thickness(0),
                Padding = new Thickness(8, 4, 8, 4)
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            rows.Children.Add(row);
            _boxes[definition.Id] = box;
        }

        scrollViewer.Content = rows;
        root.Children.Add(scrollViewer);

        var footer = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetRow(footer, 2);

        _errorText = new TextBlock
        {
            Foreground = Brushes.OrangeRed,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 10)
        };
        footer.Children.Add(_errorText);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var resetButton = new Button
        {
            Content = "Reset Defaults",
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(12, 6, 12, 6)
        };
        resetButton.Click += (_, _) => ApplyProfile(ShortcutProfile.CreateDefault());
        actions.Children.Add(resetButton);

        var cancelButton = new Button
        {
            Content = "Cancel",
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(12, 6, 12, 6),
            IsCancel = true
        };
        actions.Children.Add(cancelButton);

        var saveButton = new Button
        {
            Content = "Save",
            Padding = new Thickness(12, 6, 12, 6),
            IsDefault = true
        };
        saveButton.Click += SaveButton_Click;
        actions.Children.Add(saveButton);

        footer.Children.Add(actions);
        root.Children.Add(footer);

        Content = root;
    }

    public ShortcutProfile? ResultProfile { get; private set; }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var converter = new KeyGestureConverter();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedGestures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in ShortcutDefinitions)
        {
            var gestureText = _boxes[definition.Id].Text.Trim();
            if (string.IsNullOrWhiteSpace(gestureText))
            {
                continue;
            }

            if (converter.ConvertFromString(gestureText) is not KeyGesture)
            {
                ShowError($"Invalid shortcut for {definition.Label}: {gestureText}");
                return;
            }

            if (usedGestures.TryGetValue(gestureText, out var existingCommand))
            {
                ShowError($"Duplicate shortcut: {gestureText} is assigned to both {existingCommand} and {definition.Label}.");
                return;
            }

            usedGestures[gestureText] = definition.Label;
            result[definition.Id] = gestureText;
        }

        ResultProfile = new ShortcutProfile
        {
            Bindings = result
        };
        DialogResult = true;
        Close();
    }

    private void ApplyProfile(ShortcutProfile profile)
    {
        foreach (var definition in ShortcutDefinitions)
        {
            _boxes[definition.Id].Text = profile.Bindings.TryGetValue(definition.Id, out var value) ? value : string.Empty;
        }

        _errorText.Visibility = Visibility.Collapsed;
        _errorText.Text = string.Empty;
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _errorText.Visibility = Visibility.Visible;
    }
}
