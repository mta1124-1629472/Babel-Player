using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        if (_cache != null) return _cache;

        if (!File.Exists(_filePath)) return [];

        try
        {
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json)) return [];
            _cache = JsonSerializer.Deserialize<List<RecentSessionEntry>>(json, SerializerOptions) ?? [];
            return _cache;
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
        try
        {
            var current = (_cache ?? Load().ToList()).ToList();
            current.RemoveAll(e => e.SessionId == entry.SessionId);
            current.Insert(0, entry);
            if (current.Count > MaxEntries)
                current = current.Take(MaxEntries).ToList();

            _cache = current;
            File.WriteAllText(_filePath, JsonSerializer.Serialize(current, SerializerOptions));
        }
        catch (Exception ex)
        {
            _log.Error("RecentSessionsStore: failed to upsert recent session.", ex);
        }
    }
}
