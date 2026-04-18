using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Babel.Player.Views;

internal enum MainWindowShortcutAction
{
    None = 0,
    PlayPause,
    ToggleSubtitles,
    ToggleLeftPane,
    ToggleRightPane,
    ToggleDubMode,
    ToggleFullscreen,
    ExitFullscreen,
}

internal static class MainWindowShortcutRouter
{
    public static bool TryMap(
        Key key,
        KeyModifiers modifiers,
        IInputElement? focusedElement,
        bool isFullscreen,
        out MainWindowShortcutAction action)
    {
        action = MainWindowShortcutAction.None;

        // Escape is a safety shortcut: always exit fullscreen regardless of modifiers.
        if (key == Key.Escape && isFullscreen)
        {
            action = MainWindowShortcutAction.ExitFullscreen;
            return true;
        }

        if (modifiers != KeyModifiers.None)
            return false;

        if (key == Key.F11)
        {
            action = MainWindowShortcutAction.ToggleFullscreen;
            return true;
        }

        if (ShouldSuppressCharacterShortcut(focusedElement))
            return false;

        action = key switch
        {
            Key.Space => MainWindowShortcutAction.PlayPause,
            Key.C => MainWindowShortcutAction.ToggleSubtitles,
            Key.A => MainWindowShortcutAction.ToggleLeftPane,
            Key.S => MainWindowShortcutAction.ToggleRightPane,
            Key.D => MainWindowShortcutAction.ToggleDubMode,
            _ => MainWindowShortcutAction.None,
        };

        return action != MainWindowShortcutAction.None;
    }

    private static bool ShouldSuppressCharacterShortcut(IInputElement? focusedElement)
    {
        for (var visual = focusedElement as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is TextBox or ComboBox)
                return true;
        }

        return false;
    }
}
