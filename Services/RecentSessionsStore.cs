using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Babel.Player.Models;

namespace Babel.Player.Services;

/// <summary>
/// Maintains a capped, ordered list of recently-opened sessions in <c>recent-sessions.json</c>.
/// </summary>
public sealed class RecentSessionsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private const int MaxEntries = 10;

    private readonly string _filePath;
    private readonly AppLog _log;
    private readonly Lock _gate = new();
    private List<RecentSessionEntry>? _cache;

    public RecentSessionsStore(string filePath, AppLog log)
    {
        _filePath = filePath;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    }

    /// <summary>Returns the current list, most-recently-used first. Empty if absent or unreadable.</summary>
    public IReadOnlyList<RecentSessionEntry> Load()
    {
        lock (_gate)
        {
            _cache ??= LoadCore();
            return _cache;
        }
    }

    private List<RecentSessionEntry> LoadCore()
    {
        if (!File.Exists(_filePath)) return [];

        try
        {
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json)) return [];
            return JsonSerializer.Deserialize<List<RecentSessionEntry>>(json, SerializerOptions) ?? [];
        }
        catch (JsonException ex)
        {
            RecoverUnreadableFile($"RecentSessionsStore: failed to load recent sessions from {_filePath}.", ex);
            return [];
        }
        catch (Exception ex)
        {
            _log.Warning($"RecentSessionsStore: failed to load recent sessions: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Adds or updates <paramref name="entry"/> (matched by <see cref="RecentSessionEntry.SessionId"/>),
    /// moves it to the front, and trims the list to <see cref="MaxEntries"/>.
    /// </summary>
    public void Upsert(RecentSessionEntry entry)
    {
        lock (_gate)
        {
            try
            {
                var current = _cache is not null
                    ? new List<RecentSessionEntry>(_cache)
                    : new List<RecentSessionEntry>(LoadCore());
                current.RemoveAll(e => e.SessionId == entry.SessionId);
                current.Insert(0, entry);
                if (current.Count > MaxEntries)
                    current.RemoveRange(MaxEntries, current.Count - MaxEntries);

                // Keep the live MRU responsive even if the on-disk write fails transiently.
                _cache = current;
                var json = JsonSerializer.Serialize(current, SerializerOptions);
                JsonStorePersistence.AtomicWriteText(_filePath, json);
            }
            catch (Exception ex)
            {
                _log.Error("RecentSessionsStore: failed to upsert recent session.", ex);
            }
        }
    }

    private void RecoverUnreadableFile(string statusMessage, Exception ex)
    {
        _log.Warning($"{statusMessage} {ex.Message}");

        try
        {
            var backupPath = JsonStorePersistence.MoveUnreadableFileToBackup(_filePath);
            _log.Warning($"RecentSessionsStore: unreadable recent sessions file was moved to {backupPath}.");
        }
        catch (Exception moveEx)
        {
            _log.Error($"RecentSessionsStore: failed to quarantine unreadable file '{_filePath}'.", moveEx);
        }
    }
}
