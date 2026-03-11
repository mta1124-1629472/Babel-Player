using BabelPlayer.App;
using BabelPlayer.Core;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;

namespace BabelPlayer.WinUI;

internal sealed class ShortcutEditorDialog : ContentDialog
{
    private readonly ShortcutService _shortcutService = new();
    private readonly Dictionary<string, ShortcutEditorRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _errorText;
    private readonly TextBlock _captureHintText;
    private readonly InputKeyboardSource _keyboardSource;
    private ShortcutEditorRow? _capturingRow;

    public ShortcutEditorDialog(XamlRoot xamlRoot, ShortcutProfile currentProfile)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(currentProfile);

        _keyboardSource = InputKeyboardSource.GetForIsland(xamlRoot.ContentIsland);

        XamlRoot = xamlRoot;
        Title = "Babel Player Shortcuts";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        _errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.IndianRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        _captureHintText = new TextBlock
        {
            Text = "Select Record, then press the shortcut. Escape cancels capture. Backspace or Delete clears the current binding.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.78
        };

        Content = BuildContent(currentProfile);
        PrimaryButtonClick += OnPrimaryButtonClick;
        Closed += OnClosed;
        _keyboardSource.KeyDown += OnKeyboardSourceKeyDown;
        _keyboardSource.SystemKeyDown += OnKeyboardSourceSystemKeyDown;
    }

    public ShortcutProfile? ResultProfile { get; private set; }

    private UIElement BuildContent(ShortcutProfile currentProfile)
    {
        var rows = new StackPanel
        {
            Spacing = 10
        };

        foreach (var action in ShortcutService.SupportedActions)
        {
            currentProfile.Bindings.TryGetValue(action.CommandId, out var currentBinding);

            var row = new Grid
            {
                ColumnSpacing = 12
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelStack = new StackPanel
            {
                Spacing = 2
            };
            labelStack.Children.Add(new TextBlock
            {
                Text = action.DisplayName,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            labelStack.Children.Add(new TextBlock
            {
                Text = action.Description,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap
            });
            row.Children.Add(labelStack);

            var displayBox = new TextBox
            {
                IsReadOnly = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "Not assigned"
            };
            displayBox.TextChanged += (_, _) => ClearError();
            Grid.SetColumn(displayBox, 1);
            row.Children.Add(displayBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            Grid.SetColumn(buttons, 2);

            var recordButton = new Button
            {
                Content = "Record",
                MinWidth = 86
            };

            var clearButton = new Button
            {
                Content = "Clear"
            };

            buttons.Children.Add(recordButton);
            buttons.Children.Add(clearButton);
            row.Children.Add(buttons);
            rows.Children.Add(row);

            var editorRow = new ShortcutEditorRow(action.CommandId, displayBox, recordButton, clearButton);
            _rows[action.CommandId] = editorRow;

            recordButton.Click += (_, _) => ToggleCapture(editorRow);
            clearButton.Click += (_, _) => ClearGesture(editorRow);

            SetGesture(editorRow, currentBinding);
        }

        var resetButton = new Button
        {
            Content = "Reset Defaults",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        resetButton.Click += (_, _) =>
        {
            if (_capturingRow is not null)
            {
                StopCapture(_capturingRow, restoreDisplay: true);
            }

            ApplyProfile(ShortcutProfile.CreateDefault());
        };

        return new StackPanel
        {
            Spacing = 16,
            Width = 720,
            Children =
            {
                new TextBlock
                {
                    Text = "Edit persisted keyboard shortcuts. Recorded shortcuts stay compatible with the existing profile format.",
                    TextWrapping = TextWrapping.Wrap
                },
                _captureHintText,
                new ScrollViewer
                {
                    Height = 500,
                    Content = rows
                },
                _errorText,
                resetButton
            }
        };
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_capturingRow is not null)
        {
            ShowError("Finish recording the active shortcut or press Escape to cancel capture.");
            args.Cancel = true;
            return;
        }

        try
        {
            var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rows.Values)
            {
                if (string.IsNullOrWhiteSpace(row.GestureText))
                {
                    continue;
                }

                bindings[row.CommandId] = _shortcutService.Normalize(row.GestureText);
            }

            var profile = new ShortcutProfile
            {
                Bindings = bindings
            };

            var conflicts = _shortcutService.FindConflicts(profile);
            if (conflicts.Count > 0)
            {
                var firstConflict = conflicts[0];
                ShowError($"Duplicate shortcut: {FormatGestureForDisplay(firstConflict.Gesture)} is assigned to both {GetActionLabel(firstConflict.ExistingAction)} and {GetActionLabel(firstConflict.ConflictingAction)}.");
                args.Cancel = true;
                return;
            }

            ResultProfile = profile;
            ClearError();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            ShowError(ex.Message);
            args.Cancel = true;
        }
    }

    private void ToggleCapture(ShortcutEditorRow row)
    {
        if (_capturingRow == row)
        {
            StopCapture(row, restoreDisplay: true);
            ClearError();
            return;
        }

        if (_capturingRow is not null)
        {
            StopCapture(_capturingRow, restoreDisplay: true);
        }

        _capturingRow = row;
        row.RecordButton.Content = "Press keys...";
        row.DisplayBox.Text = "Press shortcut...";
        row.DisplayBox.SelectAll();
        _captureHintText.Text = $"Recording {GetActionLabel(row.CommandId)}. Press Escape to cancel.";
        ClearError();
    }

    private void StopCapture(ShortcutEditorRow row, bool restoreDisplay)
    {
        row.RecordButton.Content = "Record";
        if (restoreDisplay)
        {
            UpdateDisplay(row);
        }

        if (_capturingRow == row)
        {
            _capturingRow = null;
        }

        _captureHintText.Text = "Select Record, then press the shortcut. Escape cancels capture. Backspace or Delete clears the current binding.";
    }

    private void CaptureShortcut(VirtualKey key)
    {
        if (_capturingRow is null)
        {
            return;
        }

        if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu)
        {
            return;
        }

        if (key == VirtualKey.Escape && !IsCtrlPressed() && !IsAltPressed() && !IsShiftPressed())
        {
            StopCapture(_capturingRow, restoreDisplay: true);
            return;
        }

        if (key is VirtualKey.Back or VirtualKey.Delete && !IsCtrlPressed() && !IsAltPressed() && !IsShiftPressed())
        {
            ClearGesture(_capturingRow);
            StopCapture(_capturingRow, restoreDisplay: true);
            return;
        }

        if (!TryGetStorageKeyToken(key, out var keyToken))
        {
            ShowError($"Unsupported key for shortcuts: {FormatVirtualKeyForDisplay(key)}.");
            return;
        }

        var gesture = BuildGestureString(keyToken, IsCtrlPressed(), IsAltPressed(), IsShiftPressed());
        SetGesture(_capturingRow, gesture);
        StopCapture(_capturingRow, restoreDisplay: true);
        ClearError();
    }

    private void ClearGesture(ShortcutEditorRow row)
    {
        SetGesture(row, null);
        if (_capturingRow == row)
        {
            StopCapture(row, restoreDisplay: true);
        }

        ClearError();
    }

    private void ApplyProfile(ShortcutProfile profile)
    {
        foreach (var action in ShortcutService.SupportedActions)
        {
            SetGesture(
                _rows[action.CommandId],
                profile.Bindings.TryGetValue(action.CommandId, out var value) ? value : null);
        }

        ClearError();
    }

    private void SetGesture(ShortcutEditorRow row, string? gestureText)
    {
        if (string.IsNullOrWhiteSpace(gestureText))
        {
            row.GestureText = string.Empty;
            UpdateDisplay(row);
            return;
        }

        var normalized = _shortcutService.Normalize(gestureText);
        row.GestureText = normalized;
        UpdateDisplay(row);
    }

    private void UpdateDisplay(ShortcutEditorRow row)
    {
        row.DisplayBox.Text = string.IsNullOrWhiteSpace(row.GestureText)
            ? string.Empty
            : FormatGestureForDisplay(row.GestureText);
    }

    private void OnKeyboardSourceKeyDown(InputKeyboardSource sender, Microsoft.UI.Input.KeyEventArgs args)
    {
        HandleKeyCapture(args);
    }

    private void OnKeyboardSourceSystemKeyDown(InputKeyboardSource sender, Microsoft.UI.Input.KeyEventArgs args)
    {
        HandleKeyCapture(args);
    }

    private void HandleKeyCapture(Microsoft.UI.Input.KeyEventArgs args)
    {
        if (_capturingRow is null)
        {
            return;
        }

        args.Handled = true;
        CaptureShortcut(args.VirtualKey);
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _keyboardSource.KeyDown -= OnKeyboardSourceKeyDown;
        _keyboardSource.SystemKeyDown -= OnKeyboardSourceSystemKeyDown;
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _errorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        _errorText.Text = string.Empty;
        _errorText.Visibility = Visibility.Collapsed;
    }

    private static string BuildGestureString(string keyToken, bool ctrl, bool alt, bool shift)
    {
        var parts = new List<string>(4);
        if (ctrl)
        {
            parts.Add("Ctrl");
        }

        if (alt)
        {
            parts.Add("Alt");
        }

        if (shift)
        {
            parts.Add("Shift");
        }

        parts.Add(keyToken);
        return string.Join('+', parts);
    }

    private static string FormatGestureForDisplay(string gestureText)
    {
        var parts = gestureText.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        parts[^1] = FormatKeyTokenForDisplay(parts[^1]);
        return string.Join(" + ", parts);
    }

    private static string FormatKeyTokenForDisplay(string keyToken)
    {
        return keyToken switch
        {
            "OemMinus" => "-",
            "OemPlus" => "=",
            "OemComma" => ",",
            "OemPeriod" => ".",
            "D0" => "0",
            "D1" => "1",
            "D2" => "2",
            "D3" => "3",
            "D4" => "4",
            "D5" => "5",
            "D6" => "6",
            "D7" => "7",
            "D8" => "8",
            "D9" => "9",
            _ => keyToken
        };
    }

    private static string FormatVirtualKeyForDisplay(VirtualKey key)
    {
        return TryGetStorageKeyToken(key, out var token)
            ? FormatKeyTokenForDisplay(token)
            : key.ToString();
    }

    private static bool TryGetStorageKeyToken(VirtualKey key, out string token)
    {
        token = key switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Left => "Left",
            VirtualKey.Right => "Right",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.F11 => "F11",
            (VirtualKey)0xBD => "OemMinus",
            (VirtualKey)0xBB => "OemPlus",
            (VirtualKey)0xBC => "OemComma",
            (VirtualKey)0xBE => "OemPeriod",
            (VirtualKey)0x30 => "D0",
            (VirtualKey)0x31 => "D1",
            (VirtualKey)0x32 => "D2",
            (VirtualKey)0x33 => "D3",
            (VirtualKey)0x34 => "D4",
            (VirtualKey)0x35 => "D5",
            (VirtualKey)0x36 => "D6",
            (VirtualKey)0x37 => "D7",
            (VirtualKey)0x38 => "D8",
            (VirtualKey)0x39 => "D9",
            _ when key is >= (VirtualKey)0x41 and <= (VirtualKey)0x5A => ((char)key).ToString(),
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(token);
    }

    private static bool IsCtrlPressed() => IsVirtualKeyPressed(VirtualKey.Control);

    private static bool IsAltPressed() => IsVirtualKeyPressed(VirtualKey.Menu);

    private static bool IsShiftPressed() => IsVirtualKeyPressed(VirtualKey.Shift);

    private static bool IsVirtualKeyPressed(VirtualKey key)
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return state.HasFlag(CoreVirtualKeyStates.Down);
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

    private sealed class ShortcutEditorRow
    {
        public ShortcutEditorRow(string commandId, TextBox displayBox, Button recordButton, Button clearButton)
        {
            CommandId = commandId;
            DisplayBox = displayBox;
            RecordButton = recordButton;
            ClearButton = clearButton;
        }

        public string CommandId { get; }

        public TextBox DisplayBox { get; }

        public Button RecordButton { get; }

        public Button ClearButton { get; }

        public string GestureText { get; set; } = string.Empty;
    }
}
