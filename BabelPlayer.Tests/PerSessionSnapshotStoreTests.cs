using System;
using System.IO;
using System.Linq;
using Babel.Player.Models;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

/// <summary>
/// Tests for PerSessionSnapshotStore — per-session save, load, LoadAll, and graceful degradation.
/// </summary>
public sealed class PerSessionSnapshotStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _sessionsRoot;
    private readonly AppLog _log;
    private readonly PerSessionSnapshotStore _store;

    public PerSessionSnapshotStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-per-session-tests-{Guid.NewGuid():N}");
        _sessionsRoot = Path.Combine(_dir, "sessions");
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _store = new PerSessionSnapshotStore(_sessionsRoot, _log);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── Constructor ────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_CreatesSessionsRootDirectory()
    {
        Assert.True(Directory.Exists(_sessionsRoot));
    }

    // ── Save / Load ────────────────────────────────────────────────────────────

    [Fact]
    public void Save_CreatesSnapshotFileUnderSessionDirectory()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        _store.Save(snapshot);

        var expectedPath = Path.Combine(_sessionsRoot, snapshot.SessionId.ToString(), "snapshot.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Load_MissingSession_ReturnsNull()
    {
        var result = _store.Load(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_SessionId()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        _store.Save(snapshot);
        var loaded = _store.Load(snapshot.SessionId);
        Assert.NotNull(loaded);
        Assert.Equal(snapshot.SessionId, loaded.SessionId);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_Stage()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow)
            with { Stage = SessionWorkflowStage.Translated };
        _store.Save(snapshot);
        var loaded = _store.Load(snapshot.SessionId)!;
        Assert.Equal(SessionWorkflowStage.Translated, loaded.Stage);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_NullableFields()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            SourceMediaPath = "/video/input.mp4",
            TranscriptPath = "/artifacts/transcript.json",
            SourceLanguage = "es",
            TargetLanguage = "en",
            TtsVoice = "en-US-AriaNeural",
        };
        _store.Save(snapshot);
        var loaded = _store.Load(snapshot.SessionId)!;

        Assert.Equal(snapshot.SourceMediaPath, loaded.SourceMediaPath);
        Assert.Equal(snapshot.TranscriptPath, loaded.TranscriptPath);
        Assert.Equal(snapshot.SourceLanguage, loaded.SourceLanguage);
        Assert.Equal(snapshot.TargetLanguage, loaded.TargetLanguage);
        Assert.Equal(snapshot.TtsVoice, loaded.TtsVoice);
    }

    [Fact]
    public void Load_CorruptSnapshotFile_ReturnsNull()
    {
        var id = Guid.NewGuid();
        var sessionDir = Path.Combine(_sessionsRoot, id.ToString());
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "snapshot.json"), "{ not valid json {{{{");

        var result = _store.Load(id);
        Assert.Null(result);
    }

    [Fact]
    public void SaveTwice_SameSession_OverwritesPreviousSnapshot()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        _store.Save(snapshot);

        var updated = snapshot with { Stage = SessionWorkflowStage.TtsGenerated };
        _store.Save(updated);

        var loaded = _store.Load(snapshot.SessionId)!;
        Assert.Equal(SessionWorkflowStage.TtsGenerated, loaded.Stage);
    }

    // ── LoadAll ────────────────────────────────────────────────────────────────

    [Fact]
    public void LoadAll_WhenRootDoesNotExist_ReturnsEmpty()
    {
        var nonExistentRoot = Path.Combine(_dir, "does-not-exist");
        var store = new PerSessionSnapshotStore(nonExistentRoot, _log);
        // Remove it to simulate non-existent root after construction
        Directory.Delete(nonExistentRoot);

        var result = store.LoadAll();
        Assert.Empty(result);
    }

    [Fact]
    public void LoadAll_NoSnapshots_ReturnsEmpty()
    {
        var result = _store.LoadAll();
        Assert.Empty(result);
    }

    [Fact]
    public void LoadAll_ReturnsAllSavedSnapshots()
    {
        var a = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        var b = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with { Stage = SessionWorkflowStage.Transcribed };
        _store.Save(a);
        _store.Save(b);

        var all = _store.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.SessionId == a.SessionId);
        Assert.Contains(all, s => s.SessionId == b.SessionId);
    }

    [Fact]
    public void LoadAll_SkipsDirectoriesWithNoSnapshotFile()
    {
        // Create a directory that has no snapshot.json
        var emptyDir = Path.Combine(_sessionsRoot, Guid.NewGuid().ToString());
        Directory.CreateDirectory(emptyDir);

        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        _store.Save(snapshot);

        var all = _store.LoadAll();
        Assert.Single(all);
    }

    [Fact]
    public void LoadAll_SkipsCorruptSnapshotFiles()
    {
        // Write a corrupt snapshot
        var corruptId = Guid.NewGuid();
        var corruptDir = Path.Combine(_sessionsRoot, corruptId.ToString());
        Directory.CreateDirectory(corruptDir);
        File.WriteAllText(Path.Combine(corruptDir, "snapshot.json"), "bad json ;;");

        // Write a good snapshot
        var good = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        _store.Save(good);

        var all = _store.LoadAll();
        // Only the good snapshot is returned
        Assert.Single(all);
        Assert.Equal(good.SessionId, all[0].SessionId);
    }
}
