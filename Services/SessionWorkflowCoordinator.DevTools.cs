#if BABEL_DEV
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;

namespace Babel.Player.Services;

/// <summary>
/// Dev-only helpers on <see cref="SessionWorkflowCoordinator"/>.
/// Compiled only when the <c>BABEL_DEV</c> symbol is defined (dotnet build -c Dev).
/// </summary>
public partial class SessionWorkflowCoordinator
{
    private CancellationTokenSource? _devToolsCts;

    /// <summary>
    /// Exposes the application log so the in-process DevLog panel can read it.
    /// </summary>
    public AppLog DevLog => _log;

    /// <summary>
    /// Cancels any in-flight pipeline, clears all unsaved session snapshots and temp
    /// artifacts, then resets the coordinator to a clean ready state — useful during
    /// development to reproduce a "first launch" scenario without restarting the app.
    /// </summary>
    public async Task FreshStartAsync()
    {
        // 1. Cancel any running pipeline.
        if (_devToolsCts is { } existingCts)
        {
            existingCts.Cancel();
            existingCts.Dispose();
        }
        _devToolsCts = new CancellationTokenSource();
        var ct = _devToolsCts.Token;

        // 2. Give any pipeline a moment to wind down.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // 3. Wipe per-session snapshot files for the current session only.
        var sessionDir = GetSessionDirectory();
        if (!string.IsNullOrWhiteSpace(sessionDir) && Directory.Exists(sessionDir))
        {
            foreach (var file in Directory.EnumerateFiles(sessionDir, "*.json")
                                          .Concat(Directory.EnumerateFiles(sessionDir, "*.tmp")))
            {
                try { File.Delete(file); }
                catch { /* best-effort */ }
            }
        }

        // 4. Reset VM state visible in the UI to foundation.
        CurrentSession = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);

        _log?.Info("[DevTools] FreshStartAsync completed.");
    }
}
#endif
