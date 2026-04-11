using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Babel.Player.Models;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

/// <summary>
/// Tests for RecentSessionsStore — load, upsert, deduplication, capping, and round-trip serialisation.
/// </summary>
public sealed class RecentSessionsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _filePath;
    private readonly AppLog _log;
    private readonly RecentSessionsStore _store;

    public RecentSessionsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-recent-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _filePath = Path.Combine(_dir, "recent-sessions.json");
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _store = new RecentSessionsStore(_filePath, _log);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static RecentSessionEntry MakeEntry(Guid? id = null, SessionWorkflowStage stage = SessionWorkflowStage.MediaLoaded) =>
        new(
            id ?? Guid.NewGuid(),
            "/media/video.mp4",
            "video.mp4",
            stage,
            DateTimeOffset.UtcNow);

    // ── Load — missing / empty ─────────────────────────────────────────────────

    [Fact]
    public void Constructor_CreatesParentDirectory()
    {
        var nestedPath = Path.Combine(_dir, "nested", "subdir", "recent.json");
        _ = new RecentSessionsStore(nestedPath, _log);
        Assert.True(Directory.Exists(Path.GetDirectoryName(nestedPath)));
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsEmptyList()
    {
        var result = _store.Load();
        Assert.Empty(result);
    }

    [Fact]
    public void Load_WhenFileIsEmpty_ReturnsEmptyList()
    {
        File.WriteAllText(_filePath, string.Empty);
        var result = _store.Load();
        Assert.Empty(result);
    }

    [Fact]
    public void Load_WhenFileIsWhitespace_ReturnsEmptyList()
    {
        File.WriteAllText(_filePath, "   \n  ");
        var result = _store.Load();
        Assert.Empty(result);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmptyList()
    {
        File.WriteAllText(_filePath, "{ not valid json {{{{");
        var result = _store.Load();
        Assert.Empty(result);
    }

    // ── Upsert ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Upsert_NewEntry_AppearsInLoad()
    {
        var entry = MakeEntry();
        _store.Upsert(entry);

        var loaded = _store.Load();
        Assert.Single(loaded);
        Assert.Equal(entry.SessionId, loaded[0].SessionId);
    }

    [Fact]
    public void Upsert_MultipleEntries_MostRecentIsFirst()
    {
        var first = MakeEntry();
        var second = MakeEntry();
        _store.Upsert(first);
        _store.Upsert(second);

        var loaded = _store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(second.SessionId, loaded[0].SessionId);
        Assert.Equal(first.SessionId, loaded[1].SessionId);
    }

    [Fact]
    public void Upsert_DuplicateSessionId_MovesEntryToFront()
    {
        var id = Guid.NewGuid();
        var original = MakeEntry(id, SessionWorkflowStage.MediaLoaded);
        var updated = MakeEntry(id, SessionWorkflowStage.Transcribed);

        _store.Upsert(original);
        _store.Upsert(MakeEntry()); // second entry pushed to front
        _store.Upsert(updated);    // original ID moves back to front

        var loaded = _store.Load();
        Assert.Equal(id, loaded[0].SessionId);
        Assert.Equal(SessionWorkflowStage.Transcribed, loaded[0].Stage);
    }

    [Fact]
    public void Upsert_DuplicateSessionId_DoesNotAddDuplicate()
    {
        var id = Guid.NewGuid();
        _store.Upsert(MakeEntry(id));
        _store.Upsert(MakeEntry(id));

        var loaded = _store.Load();
        Assert.Single(loaded);
    }

    [Fact]
    public void Upsert_CapsMruListAtTenEntries()
    {
        for (int i = 0; i < 12; i++)
            _store.Upsert(MakeEntry());

        var loaded = _store.Load();
        Assert.Equal(10, loaded.Count);
    }

    [Fact]
    public void Upsert_MostRecentIsFrontAfterCap()
    {
        var lastId = Guid.NewGuid();
        for (int i = 0; i < 9; i++)
            _store.Upsert(MakeEntry());
        _store.Upsert(MakeEntry(lastId));

        var loaded = _store.Load();
        Assert.Equal(lastId, loaded[0].SessionId);
    }

    // ── Round-trip serialisation ───────────────────────────────────────────────

    [Fact]
    public void UpsertThenLoad_RoundTrips_AllFields()
    {
        var id = Guid.NewGuid();
        var entry = new RecentSessionEntry(
            id,
            "/path/to/media.mp4",
            "media.mp4",
            SessionWorkflowStage.Diarized,
            DateTimeOffset.Parse("2025-01-15T12:00:00Z"));

        _store.Upsert(entry);
        var loaded = _store.Load()[0];

        Assert.Equal(entry.SessionId, loaded.SessionId);
        Assert.Equal(entry.SourceMediaPath, loaded.SourceMediaPath);
        Assert.Equal(entry.SourceMediaFileName, loaded.SourceMediaFileName);
        Assert.Equal(entry.Stage, loaded.Stage);
        Assert.Equal(entry.LastUpdatedAtUtc, loaded.LastUpdatedAtUtc);
    }

    [Fact]
    public void Upsert_WritesStageAsStringName()
    {
        var entry = MakeEntry(stage: SessionWorkflowStage.Diarized);

        _store.Upsert(entry);
        using var doc = JsonDocument.Parse(File.ReadAllText(_filePath));

        Assert.Equal("Diarized", doc.RootElement[0].GetProperty("Stage").GetString());
    }

    [Fact]
    public void Load_LegacyNumericStageValue_IsAccepted()
    {
        var now = DateTimeOffset.Parse("2025-01-15T12:00:00Z");
        File.WriteAllText(
            _filePath,
            $$"""
              [
                {
                  "SessionId": "{{Guid.NewGuid()}}",
                  "SourceMediaPath": "/path/to/media.mp4",
                  "SourceMediaFileName": "media.mp4",
                  "Stage": 3,
                  "LastUpdatedAtUtc": "{{now:O}}"
                }
              ]
              """);

        var loaded = _store.Load();

        Assert.Single(loaded);
        Assert.Equal(SessionWorkflowStage.Translated, loaded[0].Stage);
    }

    [Fact]
    public void Load_ReturnsListInInsertionOrder_MostRecentFirst()
    {
        var ids = new List<Guid>();
        for (int i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            _store.Upsert(MakeEntry(id));
        }

        var loaded = _store.Load();
        // Most recently inserted is at index 0
        Assert.Equal(ids[ids.Count - 1], loaded[0].SessionId);
        Assert.Equal(ids[0], loaded[loaded.Count - 1].SessionId);
    }

    [Fact]
    public void Save_WritesValidJsonToFilePath()
    {
        _store.Upsert(MakeEntry());
        var json = File.ReadAllText(_filePath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }
}
