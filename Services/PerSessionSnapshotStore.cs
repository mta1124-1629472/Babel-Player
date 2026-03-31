using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Babel.Player.Models;

namespace Babel.Player.Services;

/// <summary>
/// Persists each session's <see cref="WorkflowSessionSnapshot"/> to a per-session directory
/// under <c>sessions/[SessionId]/snapshot.json</c>.
/// </summary>
public sealed class PerSessionSnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _sessionsRoot;
    private readonly AppLog _log;

    public PerSessionSnapshotStore(string sessionsRoot, AppLog log)
    {
        _sessionsRoot = sessionsRoot;
        _log = log;
        Directory.CreateDirectory(sessionsRoot);
    }

    /// <summary>Writes <c>sessions/[SessionId]/snapshot.json</c>. Non-fatal on failure.</summary>
    public void Save(WorkflowSessionSnapshot snapshot)
    {
        try
        {
            var dir = SessionDir(snapshot.SessionId);
            Directory.CreateDirectory(dir);
            var path = SnapshotPath(snapshot.SessionId);
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, SerializerOptions));
        }
        catch (Exception ex)
        {
            _log.Error($"PerSessionSnapshotStore: failed to save session {snapshot.SessionId}.", ex);
        }
    }

    /// <summary>
    /// Loads a single session by ID. Returns null if the file is absent or unreadable.
    /// </summary>
    public WorkflowSessionSnapshot? Load(Guid sessionId)
    {
        var path = SnapshotPath(sessionId);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WorkflowSessionSnapshot>(json, SerializerOptions);
        }
        catch (Exception ex)
        {
            _log.Warning($"PerSessionSnapshotStore: failed to load session {sessionId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads all sessions found in subdirectories of the sessions root.
    /// Directories that cannot be read are skipped and logged.
    /// </summary>
    public IReadOnlyList<WorkflowSessionSnapshot> LoadAll()
    {
        var results = new List<WorkflowSessionSnapshot>();

        if (!Directory.Exists(_sessionsRoot)) return results;

        foreach (var dir in Directory.EnumerateDirectories(_sessionsRoot))
        {
            var path = Path.Combine(dir, "snapshot.json");
            if (!File.Exists(path)) continue;

            try
            {
                var json = File.ReadAllText(path);
                var snapshot = JsonSerializer.Deserialize<WorkflowSessionSnapshot>(json, SerializerOptions);
                if (snapshot is not null)
                    results.Add(snapshot);
            }
            catch (Exception ex)
            {
                _log.Warning($"PerSessionSnapshotStore: skipped unreadable snapshot at {path}: {ex.Message}");
            }
        }

        return results;
    }

    private string SessionDir(Guid sessionId) =>
        Path.Combine(_sessionsRoot, sessionId.ToString());

    private string SnapshotPath(Guid sessionId) =>
        Path.Combine(SessionDir(sessionId), "snapshot.json");
}
