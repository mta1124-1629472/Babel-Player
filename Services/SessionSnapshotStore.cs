using System;
using System.IO;
using System.Text.Json;
using Babel.Deck.Models;

namespace Babel.Deck.Services;

public sealed class SessionSnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly AppLog _log;

    public SessionSnapshotStore(string stateFilePath, AppLog log)
    {
        StateFilePath = stateFilePath;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(stateFilePath)!);
    }

    public string StateFilePath { get; }

    public SessionSnapshotLoadResult Load()
    {
        if (!File.Exists(StateFilePath))
        {
            return new SessionSnapshotLoadResult(null, "No saved session snapshot was found. A new foundation session will be created.");
        }

        try
        {
            var json = File.ReadAllText(StateFilePath);
            var snapshot = JsonSerializer.Deserialize<WorkflowSessionSnapshot>(json, SerializerOptions);

            if (snapshot is null)
            {
                var emptyStatus = RecoverUnreadableState("Session snapshot file was empty or unreadable JSON. A new session was created.");
                return new SessionSnapshotLoadResult(null, emptyStatus);
            }

            return new SessionSnapshotLoadResult(snapshot, $"Loaded saved session snapshot from {StateFilePath}.");
        }
        catch (JsonException ex)
        {
            var status = RecoverUnreadableState("Session snapshot JSON was invalid. A new session was created.", ex);
            return new SessionSnapshotLoadResult(null, status);
        }
        catch (Exception ex)
        {
            // Non-JSON I/O failures (permissions, locked file, etc.) — degrade gracefully
            // rather than crashing startup. The corrupt file is left in place for diagnosis.
            _log.Error($"Failed to load session snapshot from {StateFilePath}. Starting fresh.", ex);
            return new SessionSnapshotLoadResult(null, $"Failed to load session snapshot: {ex.Message}. A new session was created.");
        }
    }

    public void Save(WorkflowSessionSnapshot snapshot)
    {
        try
        {
            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            File.WriteAllText(StateFilePath, json);
        }
        catch (Exception ex)
        {
            // Save failure is non-fatal — log and continue. The in-memory session is still valid.
            _log.Error($"Failed to save session snapshot to {StateFilePath}.", ex);
        }
    }

    private string RecoverUnreadableState(string statusMessage, Exception? ex = null)
    {
        var backupPath = $"{StateFilePath}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        File.Move(StateFilePath, backupPath, overwrite: true);

        if (ex is not null)
        {
            _log.Error(statusMessage, ex);
        }
        else
        {
            _log.Warning(statusMessage);
        }

        _log.Warning($"Unreadable session snapshot was moved to {backupPath}.");
        return $"{statusMessage} The previous file was moved to {backupPath}.";
    }
}

public sealed record SessionSnapshotLoadResult(
    WorkflowSessionSnapshot? Snapshot,
    string StatusMessage);
