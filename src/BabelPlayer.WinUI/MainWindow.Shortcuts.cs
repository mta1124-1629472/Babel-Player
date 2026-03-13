using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using BabelPlayer.App;
using BabelPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI;

namespace BabelPlayer.WinUI;

public sealed partial class MainWindow
{
    private void RootGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
        {
            e.Handled = true;
            FireAndForget(ExitFullscreenAsync());
            return;
        }

        if (ShouldIgnoreShortcutInput())
        {
            return;
        }

        if (TryHandleShortcut(new ShortcutKeyInput(e.Key, IsCtrlPressed(), IsAltPressed(), IsShiftPressed())))
        {
            e.Handled = true;
        }
    }

    private bool PlayerHost_ShortcutKeyPressed(ShortcutKeyInput input) => TryHandleShortcut(input);

    private bool TryHandleShortcut(ShortcutKeyInput input)
    {
        if (!TryGetShortcutCommand(input, out var commandId))
        {
            return false;
        }

        FireAndForget(ExecuteShortcutCommandAsync(commandId));
        return true;
    }

    private async Task ExecuteShortcutCommandAsync(string commandId)
    {
        var result = await _shortcutCommandExecutor.ExecuteAsync(commandId);
        if (result.RequiresOverlayInteraction)
        {
            RegisterFullscreenOverlayInteraction();
        }

        if (result.UpdatedPreferences is not null)
        {
            ApplyPreferencesSnapshot(result.UpdatedPreferences);
        }

        switch (result.ShellAction)
        {
            case ShortcutShellAction.ToggleFullscreen:
                if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
                {
                    await ExitFullscreenAsync();
                }
                else
                {
                    await EnterFullscreenAsync();
                }
                break;

            case ShortcutShellAction.TogglePictureInPicture:
                await SetWindowModeAsync(_windowModeService.CurrentMode == PlaybackWindowMode.PictureInPicture
                    ? PlaybackWindowMode.Standard
                    : PlaybackWindowMode.PictureInPicture);
                break;

            case ShortcutShellAction.ToggleSubtitleVisibility:
                ToggleSubtitleVisibility();
                break;
        }

        if (result.ItemToLoad is not null)
        {
            await LoadPlaybackItemAsync(result.ItemToLoad);
        }

        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            ShowStatus(result.StatusMessage, result.IsError);
        }
    }

    private bool TryGetShortcutCommand(ShortcutKeyInput input, out string commandId)
    {
        foreach (var binding in _resolvedShortcutBindings.Values)
        {
            if (!binding.Matches(input))
            {
                continue;
            }

            commandId = binding.CommandId;
            return true;
        }

        commandId = string.Empty;
        return false;
    }

    private void RebuildShortcutBindings()
    {
        _resolvedShortcutBindings.Clear();
        foreach (var binding in _shortcutProfileService.Current.NormalizedBindings)
        {
            if (!TryResolveShortcutBinding(binding.CommandId, binding.NormalizedGesture, out var resolved))
            {
                continue;
            }

            _resolvedShortcutBindings[binding.CommandId] = resolved;
        }
    }

    private bool TryResolveShortcutBinding(string commandId, string gestureText, out ResolvedShortcutBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(gestureText))
        {
            return false;
        }

        var tokens = gestureText
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var keyToken = tokens[^1];
        if (!TryParseShortcutKey(keyToken, out var key))
        {
            return false;
        }

        var ctrl = false;
        var alt = false;
        var shift = false;
        foreach (var modifier in tokens[..^1])
        {
            if (modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || modifier.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                ctrl = true;
            }
            else if (modifier.Equals("Alt", StringComparison.OrdinalIgnoreCase) || modifier.Equals("Menu", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
            }
            else if (modifier.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
            }
            else
            {
                return false;
            }
        }

        binding = new ResolvedShortcutBinding(commandId, key, ctrl, alt, shift);
        return true;
    }

    private static bool TryParseShortcutKey(string keyToken, out VirtualKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(keyToken))
        {
            return false;
        }

        switch (keyToken.Trim())
        {
            case "Space":
                key = VirtualKey.Space;
                return true;
            case "Left":
                key = VirtualKey.Left;
                return true;
            case "Right":
                key = VirtualKey.Right;
                return true;
            case "PageUp":
                key = VirtualKey.PageUp;
                return true;
            case "PageDown":
                key = VirtualKey.PageDown;
                return true;
            case "F11":
                key = VirtualKey.F11;
                return true;
            case "OemMinus":
                key = (VirtualKey)0xBD;
                return true;
            case "OemPlus":
                key = (VirtualKey)0xBB;
                return true;
            case "OemComma":
                key = (VirtualKey)0xBC;
                return true;
            case "OemPeriod":
                key = (VirtualKey)0xBE;
                return true;
            case "D0":
                key = (VirtualKey)0x30;
                return true;
        }

        if (keyToken.Length == 1)
        {
            var upper = char.ToUpperInvariant(keyToken[0]);
            if (upper is >= 'A' and <= 'Z')
            {
                key = (VirtualKey)upper;
                return true;
            }
        }

        return Enum.TryParse(keyToken, true, out key);
    }

    private bool ShouldIgnoreShortcutInput()
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        return focused is TextBox or PasswordBox or RichEditBox or ComboBox;
    }

    private static bool IsCtrlPressed() => IsVirtualKeyPressed(VirtualKey.Control);

    private static bool IsAltPressed() => IsVirtualKeyPressed(VirtualKey.Menu);

    private static bool IsShiftPressed() => IsVirtualKeyPressed(VirtualKey.Shift);

    private static bool IsVirtualKeyPressed(VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private async void EditShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var updatedProfile = await _credentialDialogService.EditShortcutsAsync(_shortcutProfileService.Current.Profile);
        if (updatedProfile is null)
        {
            return;
        }

        PersistShortcutProfile(updatedProfile);
        RebuildShortcutBindings();
        ShowStatus("Keyboard shortcuts updated.");
    }

    private readonly record struct ResolvedShortcutBinding(string CommandId, VirtualKey Key, bool Ctrl, bool Alt, bool Shift)
    {
        public bool Matches(ShortcutKeyInput input)
        {
            return input.Key == Key
                && input.Ctrl == Ctrl
                && input.Alt == Alt
                && input.Shift == Shift;
        }
    }
}

