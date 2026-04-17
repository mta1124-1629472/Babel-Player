using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed class SessionSnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly AppLog _log;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

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
            var snapshot = SessionSnapshotJsonCompat.Deserialize(json, SerializerOptions);

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
        // Serialize outside the gate — JSON serialization is CPU-bound and does not
        // need to block other pending saves. The gate guards the file write itself so
        // Save() and SaveAsync() cannot clobber each other at StateFilePath.
        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        _saveGate.Wait();
        try
        {
            File.WriteAllText(StateFilePath, json);
        }
        catch (Exception ex)
        {
            // Save failure is non-fatal — log and continue. The in-memory session is still valid.
            _log.Error($"Failed to save session snapshot to {StateFilePath}.", ex);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Asynchronous counterpart to <see cref="Save"/>. Use from async pipeline code to avoid
    /// blocking the caller on disk I/O. Non-fatal on failure (errors are logged and swallowed).
    /// </summary>
    public async Task SaveAsync(WorkflowSessionSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(StateFilePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save session snapshot to {StateFilePath}.", ex);
        }
        finally
        {
            _saveGate.Release();
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
