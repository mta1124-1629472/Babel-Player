using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Decouples pipeline/workflow error reporting from the Avalonia UI layer.
/// Implementations show a modal dialog; a no-op stub is used in tests.
/// </summary>
public interface IErrorDialogService
{
    /// <summary>
    /// Display a full-detail error to the user (modal dialog or equivalent).
    /// Must be safe to call from any thread — implementations marshal to the UI thread.
    /// </summary>
    /// <param name="title">Short summary, e.g. "NLLB Translation failed".</param>
    /// <param name="fullDetail">Full exception text including inner exceptions and stack trace.</param>
    /// <param name="logFilePath">Path of the log file so the user can open it directly.</param>
    Task ShowErrorAsync(string title, string fullDetail, string? logFilePath = null);
}
