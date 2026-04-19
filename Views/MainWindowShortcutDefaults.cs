using Avalonia.Input;

namespace Babel.Player.Views;

internal static class MainWindowShortcutDefaults
{
    public const Key PlayPauseKey = Key.Space;
    public const Key ToggleSubtitlesKey = Key.C;
    public const Key ToggleLeftPaneKey = Key.A;
    public const Key ToggleRightPaneKey = Key.S;
    public const Key ToggleDubModeKey = Key.D;
    public const Key ToggleFullscreenKey = Key.F11;

    public const string PlayPauseLabel = "Space";
    public const string ToggleSubtitlesLabel = "C";
    public const string ToggleLeftPaneLabel = "A";
    public const string ToggleRightPaneLabel = "S";
    public const string ToggleDubModeLabel = "D";
    public const string ToggleFullscreenLabel = "F11";
}
