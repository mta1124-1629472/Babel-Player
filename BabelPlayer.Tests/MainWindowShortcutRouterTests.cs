using Avalonia.Controls;
using Avalonia.Input;
using Babel.Player.Views;

namespace BabelPlayer.Tests;

public sealed class MainWindowShortcutRouterTests
{
    [Fact]
    public void TryMap_MapsAdvertisedWindowShortcuts()
    {
        AssertShortcut(Key.Space, KeyModifiers.None, new Button(), isFullscreen: false, MainWindowShortcutAction.PlayPause);
        AssertShortcut(Key.C, KeyModifiers.None, new Button(), isFullscreen: false, MainWindowShortcutAction.ToggleSubtitles);
        AssertShortcut(Key.S, KeyModifiers.None, new Button(), isFullscreen: false, MainWindowShortcutAction.ToggleSegmentPane);
        AssertShortcut(Key.D, KeyModifiers.None, new Button(), isFullscreen: false, MainWindowShortcutAction.ToggleDubMode);
        AssertShortcut(Key.F11, KeyModifiers.None, new ComboBox(), isFullscreen: false, MainWindowShortcutAction.ToggleFullscreen);
        AssertShortcut(Key.Escape, KeyModifiers.None, new ComboBox(), isFullscreen: true, MainWindowShortcutAction.ExitFullscreen);
    }

    [Fact]
    public void TryMap_SuppressesCharacterShortcuts_WhenInputControlIsFocused()
    {
        var handled = MainWindowShortcutRouter.TryMap(
            Key.C,
            KeyModifiers.None,
            new ComboBox(),
            isFullscreen: false,
            out var action);

        Assert.False(handled);
        Assert.Equal(MainWindowShortcutAction.None, action);
    }

    [Fact]
    public void TryMap_IgnoresModifiedKeys()
    {
        var handled = MainWindowShortcutRouter.TryMap(
            Key.Space,
            KeyModifiers.Control,
            new Button(),
            isFullscreen: false,
            out var action);

        Assert.False(handled);
        Assert.Equal(MainWindowShortcutAction.None, action);
    }

    [Fact]
    public void TryMap_EscapeExitsFullscreen_EvenWithModifiersHeld()
    {
        AssertShortcut(Key.Escape, KeyModifiers.Shift, new Button(), isFullscreen: true, MainWindowShortcutAction.ExitFullscreen);
        AssertShortcut(Key.Escape, KeyModifiers.Control, new Button(), isFullscreen: true, MainWindowShortcutAction.ExitFullscreen);
        AssertShortcut(Key.Escape, KeyModifiers.Alt, new Button(), isFullscreen: true, MainWindowShortcutAction.ExitFullscreen);
    }

    private static void AssertShortcut(
        Key key,
        KeyModifiers modifiers,
        IInputElement focusedElement,
        bool isFullscreen,
        MainWindowShortcutAction expectedAction)
    {
        var handled = MainWindowShortcutRouter.TryMap(
            key,
            modifiers,
            focusedElement,
            isFullscreen,
            out var action);

        Assert.True(handled);
        Assert.Equal(expectedAction, action);
    }
}
