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

        // Panes are not visible in fullscreen; do not map shortcuts that would still persist layout.
        if (isFullscreen
            && (key == MainWindowShortcutDefaults.ToggleLeftPaneKey
                || key == MainWindowShortcutDefaults.ToggleRightPaneKey))
        {
            return false;
        }

        if (key == MainWindowShortcutDefaults.ToggleFullscreenKey)
        {
            action = MainWindowShortcutAction.ToggleFullscreen;
            return true;
        }

        if (ShouldSuppressCharacterShortcut(focusedElement))
            return false;

        action = key switch
        {
            MainWindowShortcutDefaults.PlayPauseKey => MainWindowShortcutAction.PlayPause,
            MainWindowShortcutDefaults.ToggleSubtitlesKey => MainWindowShortcutAction.ToggleSubtitles,
            MainWindowShortcutDefaults.ToggleLeftPaneKey => MainWindowShortcutAction.ToggleLeftPane,
            MainWindowShortcutDefaults.ToggleRightPaneKey => MainWindowShortcutAction.ToggleRightPane,
            MainWindowShortcutDefaults.ToggleDubModeKey => MainWindowShortcutAction.ToggleDubMode,
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
