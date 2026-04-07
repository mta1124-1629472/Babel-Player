namespace Babel.Player;

/// <summary>
/// Centralises application identity strings so that build-configuration-specific
/// labels (e.g. the [DEV] suffix) are defined in exactly one place.
/// </summary>
public static class AppIdentity
{
    /// <summary>
    /// The display name shown in window titles, about dialogs, and task-bar entries.
    /// Returns "Babel-Player [DEV]" when compiled with BABEL_DEV; "Babel-Player" otherwise.
    /// </summary>
    public static string AppName =>
#if BABEL_DEV
        "Babel-Player [DEV]";
#else
        "Babel-Player";
#endif

    /// <summary>
    /// Stable non-display product identifier without any build-configuration suffix —
    /// suitable for file paths, log file prefixes, registry keys, and other persisted names.
    /// This intentionally matches the existing persisted identifier form ("BabelPlayer").
    /// </summary>
    public const string ProductName = "BabelPlayer";
}
