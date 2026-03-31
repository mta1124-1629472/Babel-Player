using System;
using Babel.Player.Models;

namespace Babel.Player.Models;

/// <summary>
/// Lightweight index entry stored in <c>recent-sessions.json</c>.
/// Carries only the display-relevant fields; full state lives in per-session snapshot files.
/// </summary>
public sealed record RecentSessionEntry(
    Guid SessionId,
    string SourceMediaPath,
    string SourceMediaFileName,
    SessionWorkflowStage Stage,
    DateTimeOffset LastUpdatedAtUtc);
