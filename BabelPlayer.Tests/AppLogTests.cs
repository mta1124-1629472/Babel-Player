using System;
using System.IO;
using System.Linq;
using System.Text;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

/// <summary>
/// Tests for AppLog — file creation, log level output, appending, and log rotation.
/// </summary>
public sealed class AppLogTests : IDisposable
{
    private readonly string _dir;
    private readonly string _logPath;

    public AppLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-applog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _logPath = Path.Combine(_dir, "test.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── Constructor ────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_CreatesLogDirectory()
    {
        var nestedPath = Path.Combine(_dir, "nested", "logs", "app.log");
        _ = new AppLog(nestedPath);
        Assert.True(Directory.Exists(Path.GetDirectoryName(nestedPath)));
    }

    [Fact]
    public void LogFilePath_ReturnsSuppliedPath()
    {
        var log = new AppLog(_logPath);
        Assert.Equal(_logPath, log.LogFilePath);
    }

    // ── Write operations ───────────────────────────────────────────────────────

    [Fact]
    public void Info_WritesEntryToLogFile()
    {
        var log = new AppLog(_logPath);
        log.Info("hello from info");

        var content = File.ReadAllText(_logPath);
        Assert.Contains("hello from info", content);
    }

    [Fact]
    public void Info_EntryContainsInfoLevel()
    {
        var log = new AppLog(_logPath);
        log.Info("test message");

        var content = File.ReadAllText(_logPath);
        Assert.Contains("[INFO]", content);
    }

    [Fact]
    public void Warning_WritesEntryToLogFile()
    {
        var log = new AppLog(_logPath);
        log.Warning("a warning occurred");

        var content = File.ReadAllText(_logPath);
        Assert.Contains("a warning occurred", content);
    }

    [Fact]
    public void Warning_EntryContainsWarnLevel()
    {
        var log = new AppLog(_logPath);
        log.Warning("test warning");

        var content = File.ReadAllText(_logPath);
        Assert.Contains("[WARN]", content);
    }

    [Fact]
    public void Error_WritesMessageAndExceptionToLogFile()
    {
        var log = new AppLog(_logPath);
        log.Error("something failed", new InvalidOperationException("bang"));

        var content = File.ReadAllText(_logPath);
        Assert.Contains("something failed", content);
        Assert.Contains("bang", content);
    }

    [Fact]
    public void Error_EntryContainsErrorLevel()
    {
        var log = new AppLog(_logPath);
        log.Error("error msg", new Exception("ex"));

        var content = File.ReadAllText(_logPath);
        Assert.Contains("[ERROR]", content);
    }

    [Fact]
    public void MultipleEntries_AreAllAppended()
    {
        var log = new AppLog(_logPath);
        log.Info("entry one");
        log.Warning("entry two");
        log.Info("entry three");

        var content = File.ReadAllText(_logPath);
        Assert.Contains("entry one", content);
        Assert.Contains("entry two", content);
        Assert.Contains("entry three", content);
    }

    [Fact]
    public void Entries_ContainIso8601Timestamp()
    {
        var log = new AppLog(_logPath);
        log.Info("timestamped");

        var content = File.ReadAllText(_logPath);
        // ISO 8601 pattern: digits-digits-digitsT
        Assert.Matches(@"\d{4}-\d{2}-\d{2}T", content);
    }

    // ── Rotation ───────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_LogFileBelowThreshold_DoesNotRotate()
    {
        // Write a small log file (< 10 MB) before constructing AppLog
        File.WriteAllText(_logPath, "existing log content");
        _ = new AppLog(_logPath);

        // The file should still exist (not rotated) and contain the original content
        Assert.True(File.Exists(_logPath));
        var content = File.ReadAllText(_logPath);
        Assert.Contains("existing log content", content);
    }

    [Fact]
    public void Constructor_LogFileExceedsThreshold_RotatesToArchive()
    {
        // Write a file that exceeds 10 MB
        var bigContent = new string('x', 11 * 1024 * 1024);
        File.WriteAllText(_logPath, bigContent);

        _ = new AppLog(_logPath);

        // Original log file should be gone (moved to archive)
        Assert.False(File.Exists(_logPath), "Log file should have been rotated away.");

        // An archive file matching the stem should exist
        var stem = Path.GetFileNameWithoutExtension(_logPath);
        var ext = Path.GetExtension(_logPath);
        var archives = Directory.GetFiles(_dir, $"{stem}.*{ext}");
        Assert.NotEmpty(archives);
    }

    [Fact]
    public void Constructor_AfterRotation_CanStillWrite()
    {
        var bigContent = new string('y', 11 * 1024 * 1024);
        File.WriteAllText(_logPath, bigContent);

        var log = new AppLog(_logPath);
        log.Info("post-rotation entry");

        Assert.True(File.Exists(_logPath));
        Assert.Contains("post-rotation entry", File.ReadAllText(_logPath));
    }

    [Fact]
    public void Constructor_RotationPrunesOldArchives_KeepsAtMostFour()
    {
        var stem = Path.GetFileNameWithoutExtension(_logPath);
        var ext = Path.GetExtension(_logPath);

        // Pre-create 5 existing archive files (simulating 5 old archives)
        for (int i = 0; i < 5; i++)
        {
            var archiveName = Path.Combine(_dir, $"{stem}.2025010{i}-000000{ext}");
            File.WriteAllText(archiveName, "old archive");
        }

        // Now write an oversized current log and trigger rotation
        var bigContent = new string('z', 11 * 1024 * 1024);
        File.WriteAllText(_logPath, bigContent);
        _ = new AppLog(_logPath);

        var archives = Directory.GetFiles(_dir, $"{stem}.*{ext}");
        Assert.True(archives.Length <= 4, $"Expected at most 4 archives, found {archives.Length}.");
    }
}
