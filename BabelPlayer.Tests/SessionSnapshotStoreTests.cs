using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Babel.Player.Models;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

/// <summary>
/// Tests for SessionSnapshotStore — persistence, round-trip serialisation,
/// and graceful degradation on corrupt or missing files.
/// </summary>
public sealed class SessionSnapshotStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _stateFile;
    private readonly AppLog _log;
    private readonly SessionSnapshotStore _store;

    public SessionSnapshotStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-store-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _stateFile = Path.Combine(_dir, "state", "session.json");
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _store = new SessionSnapshotStore(_stateFile, _log);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── Constructor ────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_CreatesParentDirectory()
    {
        Assert.True(Directory.Exists(Path.GetDirectoryName(_stateFile)));
    }

    [Fact]
    public void StateFilePath_ReturnsSuppliedPath()
    {
        Assert.Equal(_stateFile, _store.StateFilePath);
    }

    // ── Load — missing file ────────────────────────────────────────────────────

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsNullSnapshot()
    {
        var result = _store.Load();
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_StatusMessageMentionsNewSession()
    {
        var result = _store.Load();
        Assert.Contains("new", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── Save → Load round-trip ─────────────────────────────────────────────────

    [Fact]
    public void SaveThenLoad_RoundTrips_SessionId()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        _store.Save(snapshot);
        var loaded = _store.Load().Snapshot;
        Assert.NotNull(loaded);
        Assert.Equal(snapshot.SessionId, loaded.SessionId);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_Stage()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow)
            with { Stage = SessionWorkflowStage.Transcribed };
        _store.Save(snapshot);
        var loaded = _store.Load().Snapshot!;
        Assert.Equal(SessionWorkflowStage.Transcribed, loaded.Stage);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_NullableFields()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = WorkflowSessionSnapshot.CreateNew(now) with
        {
            SourceMediaPath = "/media/input.mp4",
            IngestedMediaPath = "/media/ingested.mp4",
            TranscriptPath = "/artifacts/transcript.json",
            TranslationPath = "/artifacts/translation.json",
            SourceLanguage = "ja",
            TargetLanguage = "en",
            TtsPath = "/artifacts/tts.wav",
            TtsVoice = "en-US-Neural",
        };
        _store.Save(snapshot);
        var loaded = _store.Load().Snapshot!;

        Assert.Equal(snapshot.SourceMediaPath, loaded.SourceMediaPath);
        Assert.Equal(snapshot.IngestedMediaPath, loaded.IngestedMediaPath);
        Assert.Equal(snapshot.TranscriptPath, loaded.TranscriptPath);
        Assert.Equal(snapshot.TranslationPath, loaded.TranslationPath);
        Assert.Equal(snapshot.SourceLanguage, loaded.SourceLanguage);
        Assert.Equal(snapshot.TargetLanguage, loaded.TargetLanguage);
        Assert.Equal(snapshot.TtsPath, loaded.TtsPath);
        Assert.Equal(snapshot.TtsVoice, loaded.TtsVoice);
    }

    [Fact]
    public void SaveThenLoad_StatusMessage_MentionsLoadedPath()
    {
        _store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow));
        var result = _store.Load();
        Assert.Contains(_stateFile, result.StatusMessage);
    }

    [Fact]
    public void Save_WritesValidJsonToStateFilePath()
    {
        _store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow));
        var json = File.ReadAllText(_stateFile);
        // Should not throw
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void SaveTwice_OverwritesPreviousSnapshot()
    {
        var first = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        _store.Save(first);

        var second = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow)
            with { Stage = SessionWorkflowStage.TtsGenerated };
        _store.Save(second);

        var loaded = _store.Load().Snapshot!;
        Assert.Equal(second.SessionId, loaded.SessionId);
        Assert.Equal(SessionWorkflowStage.TtsGenerated, loaded.Stage);
    }

    // ── Load — corrupt JSON ────────────────────────────────────────────────────

    [Fact]
    public void Load_CorruptJson_ReturnsNullSnapshot()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, "{ not valid json {{{{");
        var result = _store.Load();
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Load_CorruptJson_MovesFileToBackup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, "{ not valid json {{{{");
        _store.Load();

        Assert.False(File.Exists(_stateFile), "Original corrupt file should have been moved.");
        var stateDir = Path.GetDirectoryName(_stateFile)!;
        var backups = Directory.GetFiles(stateDir, "*.corrupt.*");
        Assert.Single(backups);
    }

    [Fact]
    public void Load_CorruptJson_StatusMessageMentionsCorruptFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, "{}}}");
        var result = _store.Load();
        // The status message should acknowledge the problem
        Assert.False(string.IsNullOrWhiteSpace(result.StatusMessage));
        Assert.Contains("new session", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── Load — empty / whitespace file ────────────────────────────────────────

    [Fact]
    public void Load_EmptyFile_ReturnsNullSnapshotAndMovesToBackup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, string.Empty);
        var result = _store.Load();

        Assert.Null(result.Snapshot);
        Assert.False(File.Exists(_stateFile));
    }

    // ── Load — snapshot has null-valued root (JSON literal null) ──────────────

    [Fact]
    public void Load_JsonNullLiteral_ReturnsNullSnapshotAndMovesToBackup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, "null");
        var result = _store.Load();

        Assert.Null(result.Snapshot);
        // The file should have been moved to backup (empty/unreadable path)
        Assert.False(File.Exists(_stateFile));
    }

    // ── Backup file naming ─────────────────────────────────────────────────────

    [Fact]
    public void Load_CorruptJson_BackupFileNameContainsTimestamp()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, "bad");
        _store.Load();

        var stateDir = Path.GetDirectoryName(_stateFile)!;
        var backup = Directory.GetFiles(stateDir, "*.corrupt.*").Single();
        // Backup name should embed a datestamp (yyyyMMdd portion)
        Assert.Matches(@"\d{8}", Path.GetFileName(backup));
    }

    // ── StatusMessage content ──────────────────────────────────────────────────

    [Fact]
    public void Load_ValidFile_StatusMessageIsNotEmpty()
    {
        _store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow));
        var result = _store.Load();
        Assert.False(string.IsNullOrWhiteSpace(result.StatusMessage));
    }

    [Fact]
    public void Load_MissingFile_StatusMessageIsNotEmpty()
    {
        var result = _store.Load();
        Assert.False(string.IsNullOrWhiteSpace(result.StatusMessage));
    }
}
