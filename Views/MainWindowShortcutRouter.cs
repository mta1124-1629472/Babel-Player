using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Babel.Player.Views;

internal enum MainWindowShortcutAction
{
    None = 0,
    PlayPause,
    ToggleSegmentPane,
    ToggleDubMode,
    ToggleFullscreen,
    ExitFullscreen,
}

internal static class MainWindowShortcutRouter
{
    /// <summary>
    /// Maps a keyboard event to a MainWindowShortcutAction when the input corresponds to a main-window shortcut.
    /// </summary>
    /// <param name="key">The key that was pressed.</param>
    /// <param name="modifiers">Active modifier keys; mapping is suppressed when this is not <c>KeyModifiers.None</c>.</param>
    /// <param name="focusedElement">The currently focused input element; character shortcuts are suppressed when focus is inside editable controls (e.g., text box or combo box).</param>
    /// <param name="isFullscreen">Whether the window is currently fullscreen; affects fullscreen-related shortcuts.</param>
    /// <param name="action">When the method returns, contains the mapped MainWindowShortcutAction or <c>MainWindowShortcutAction.None</c> if no shortcut applies.</param>
    /// <returns><c>true</c> if a shortcut was mapped and <paramref name="action"/> is not <c>MainWindowShortcutAction.None</c>, <c>false</c> otherwise.</returns>
    public static bool TryMap(
        Key key,
        KeyModifiers modifiers,
        IInputElement? focusedElement,
        bool isFullscreen,
        out MainWindowShortcutAction action)
    {
        action = MainWindowShortcutAction.None;

        if (modifiers != KeyModifiers.None)
            return false;

        if (key == Key.Escape && isFullscreen)
        {
            action = MainWindowShortcutAction.ExitFullscreen;
            return true;
        }

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
            Key.S => MainWindowShortcutAction.ToggleSegmentPane,
            Key.D => MainWindowShortcutAction.ToggleDubMode,
            _ => MainWindowShortcutAction.None,
        };

        return action != MainWindowShortcutAction.None;
    }

    /// <summary>
    /// Determines whether character-based keyboard shortcuts should be suppressed because the focused element is or is inside editable input controls.
    /// </summary>
    /// <param name="focusedElement">The currently focused input element, or null if none.</param>
    /// <returns>`true` if the focused element or any of its visual ancestors is a <see cref="TextBox"/> or <see cref="ComboBox"/>; `false` otherwise.</returns>
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
