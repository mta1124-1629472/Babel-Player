using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Babel.Player.Models;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Tests for <see cref="SessionSwitchService"/> — session stashing, loading, caching,
/// and cache eviction.
/// </summary>
public sealed class SessionSwitchServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;
    private readonly SessionSwitchService _svc;

    public SessionSwitchServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-switch-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _perSessionStore = new PerSessionSnapshotStore(Path.Combine(_dir, "sessions"), _log);
        _recentStore = new RecentSessionsStore(Path.Combine(_dir, "recent.json"), _log);
        _svc = new SessionSwitchService(_perSessionStore, _recentStore, _log);
    }

    public void Dispose()
    {
        try { _log.Dispose(); }
        catch { }
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    private static WorkflowSessionSnapshot MakeSession(string? mediaPath = null) =>
        WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            SourceMediaPath = mediaPath,
            Stage = mediaPath is null ? SessionWorkflowStage.Foundation : SessionWorkflowStage.MediaLoaded,
        };

    // ── StashCurrentSession — no media path ───────────────────────────────────

    [Fact]
    public void StashCurrentSession_NoMediaPath_ReturnsCurrentRecentList()
    {
        var session = MakeSession(mediaPath: null);
        var cache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);

        var result = _svc.StashCurrentSession(session, cache, cacheLimit: 5);

        // With no media path the session is not stashed — result is whatever recent list contains
        Assert.NotNull(result);
        Assert.Empty(cache); // cache unchanged
    }

    // ── StashCurrentSession — with media path ─────────────────────────────────

    [Fact]
    public void StashCurrentSession_WithMediaPath_AddsToCache()
    {
        var mediaPath = Path.Combine(_dir, "video.mp4");
        var session = MakeSession(mediaPath: mediaPath);
        var cache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);

        _svc.StashCurrentSession(session, cache, cacheLimit: 5);

        Assert.Single(cache);
    }

    [Fact]
    public void StashCurrentSession_WithMediaPath_PersistsToPerSessionStore()
    {
        var mediaPath = Path.Combine(_dir, "video.mp4");
        var session = MakeSession(mediaPath: mediaPath);
        var cache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);

        _svc.StashCurrentSession(session, cache, cacheLimit: 5);

        var loaded = _perSessionStore.Load(session.SessionId);
        Assert.NotNull(loaded);
        Assert.Equal(session.SessionId, loaded.SessionId);
    }

    [Fact]
    public void StashCurrentSession_WithMediaPath_AddsToRecentList()
    {
        var mediaPath = Path.Combine(_dir, "video.mp4");
        var session = MakeSession(mediaPath: mediaPath);
        var cache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);

        var recent = _svc.StashCurrentSession(session, cache, cacheLimit: 5);

        Assert.Single(recent);
        Assert.Equal(session.SessionId, recent[0].SessionId);
    }

    // ── StashCurrentSession — cache eviction ──────────────────────────────────

    [Fact]
    public void StashCurrentSession_ExceedsCacheLimit_EvictsOldestEntry()
    {
        var cache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);

        var firstPath = Path.Combine(_dir, "video0.mp4");
        var firstKey = Path.GetFullPath(firstPath);
        var firstSession = MakeSession(mediaPath: firstPath);
        _svc.StashCurrentSession(firstSession, cache, cacheLimit: 2);

        var secondPath = Path.Combine(_dir, "video1.mp4");
        var secondKey = Path.GetFullPath(secondPath);
        var secondSession = MakeSession(mediaPath: secondPath);
        _svc.StashCurrentSession(secondSession, cache, cacheLimit: 2);

        Assert.Equal(2, cache.Count);
        Assert.Contains(firstKey, cache.Keys);
        Assert.Contains(secondKey, cache.Keys);

        // Adding a third should evict the oldest entry (the first one added)
        var extraPath = Path.Combine(_dir, "video_extra.mp4");
        var extraKey = Path.GetFullPath(extraPath);
        var extraSession = MakeSession(mediaPath: extraPath);
        _svc.StashCurrentSession(extraSession, cache, cacheLimit: 2);

        Assert.Equal(2, cache.Count); // still at limit after eviction
        Assert.DoesNotContain(firstKey, cache.Keys);
        Assert.Contains(secondKey, cache.Keys);
        Assert.Contains(extraKey, cache.Keys);
    }

    // ── LoadSession ───────────────────────────────────────────────────────────

    [Fact]
    public void LoadSession_SessionInCache_ReturnsCachedSnapshot()
    {
        var mediaPath = Path.Combine(_dir, "video.mp4");
        var session = MakeSession(mediaPath: mediaPath);
        var cache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);
        _svc.StashCurrentSession(session, cache, cacheLimit: 5);

        var loaded = _svc.LoadSession(session.SessionId, cache);

        Assert.NotNull(loaded);
        Assert.Equal(session.SessionId, loaded.SessionId);
    }

    [Fact]
    public void LoadSession_NotInCache_LoadsFromPerSessionStore()
    {
        var session = MakeSession(mediaPath: Path.Combine(_dir, "video.mp4"));
        _perSessionStore.Save(session);

        var emptyCache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);
        var loaded = _svc.LoadSession(session.SessionId, emptyCache);

        Assert.NotNull(loaded);
        Assert.Equal(session.SessionId, loaded.SessionId);
    }

    [Fact]
    public void LoadSession_UnknownSessionId_ReturnsNull()
    {
        var emptyCache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);
        var result = _svc.LoadSession(Guid.NewGuid(), emptyCache);
        Assert.Null(result);
    }

    // ── LoadSessionForMedia ───────────────────────────────────────────────────

    [Fact]
    public void LoadSessionForMedia_MediaPathInCache_ReturnsSnapshot()
    {
        var mediaPath = Path.Combine(_dir, "video.mp4");
        var session = MakeSession(mediaPath: mediaPath);
        var cache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);
        _svc.StashCurrentSession(session, cache, cacheLimit: 5);

        var result = _svc.LoadSessionForMedia(mediaPath, cache);

        Assert.NotNull(result);
        Assert.Equal(session.SessionId, result.SessionId);
    }

    [Fact]
    public void LoadSessionForMedia_MediaPathNotInCache_ReturnsNull()
    {
        var emptyCache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);
        var result = _svc.LoadSessionForMedia(Path.Combine(_dir, "not-loaded.mp4"), emptyCache);
        Assert.Null(result);
    }

    // ── CacheCurrentSession ───────────────────────────────────────────────────

    [Fact]
    public void CacheCurrentSession_AddsSessionToCache()
    {
        var mediaPath = Path.Combine(_dir, "video.mp4");
        var session = MakeSession(mediaPath: mediaPath);
        var cache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);

        _svc.CacheCurrentSession(mediaPath, session, cache, cacheLimit: 5);

        Assert.Single(cache);
    }

    [Fact]
    public void CacheCurrentSession_UpdatesExistingCacheEntry()
    {
        var mediaPath = Path.Combine(_dir, "video.mp4");
        var original = MakeSession(mediaPath: mediaPath);
        var cache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);

        _svc.CacheCurrentSession(mediaPath, original, cache, cacheLimit: 5);

        var updated = original with { Stage = SessionWorkflowStage.Transcribed };
        _svc.CacheCurrentSession(mediaPath, updated, cache, cacheLimit: 5);

        Assert.Single(cache);
        var loaded = _svc.LoadSessionForMedia(mediaPath, cache);
        Assert.Equal(SessionWorkflowStage.Transcribed, loaded!.Stage);
    }

    // ── GetSessionDirectory ───────────────────────────────────────────────────

    [Fact]
    public void GetSessionDirectory_ReturnsPathUnderSessionsRoot()
    {
        var sessionId = Guid.NewGuid();
        var sessionDir = _svc.GetSessionDirectory(sessionId);
        Assert.Contains(sessionId.ToString(), sessionDir);
    }

    // ── Cache eviction order (FIFO) ───────────────────────────────────────────

    [Fact]
    public void CacheCurrentSession_ExceedsCacheLimit_EvictsOldestEntry()
    {
        var cache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);

        var path1 = Path.Combine(_dir, "media1.mp4");
        var path2 = Path.Combine(_dir, "media2.mp4");
        var path3 = Path.Combine(_dir, "media3.mp4");
        var key1 = Path.GetFullPath(path1);
        var key2 = Path.GetFullPath(path2);
        var key3 = Path.GetFullPath(path3);

        _svc.CacheCurrentSession(path1, MakeSession(mediaPath: path1), cache, cacheLimit: 2);
        _svc.CacheCurrentSession(path2, MakeSession(mediaPath: path2), cache, cacheLimit: 2);

        Assert.Equal(2, cache.Count);

        // Adding a third exceeds the limit — oldest (path1) must be evicted
        _svc.CacheCurrentSession(path3, MakeSession(mediaPath: path3), cache, cacheLimit: 2);

        Assert.Equal(2, cache.Count);
        Assert.DoesNotContain(key1, cache.Keys);
        Assert.Contains(key2, cache.Keys);
        Assert.Contains(key3, cache.Keys);
    }

    [Fact]
    public void CacheCurrentSession_UpdateExistingKey_DoesNotCauseDoubleEviction()
    {
        // Updating an existing key should not push it to the back of the insertion queue
        // so the eviction order remains stable.
        var cache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);

        var path1 = Path.Combine(_dir, "media_upd1.mp4");
        var path2 = Path.Combine(_dir, "media_upd2.mp4");
        var key1 = Path.GetFullPath(path1);
        var key2 = Path.GetFullPath(path2);

        var session1 = MakeSession(mediaPath: path1);
        _svc.CacheCurrentSession(path1, session1, cache, cacheLimit: 2);
        _svc.CacheCurrentSession(path2, MakeSession(mediaPath: path2), cache, cacheLimit: 2);

        // Update path1 (already in cache) — count should remain 2
        var updatedSession1 = session1 with { Stage = SessionWorkflowStage.Transcribed };
        _svc.CacheCurrentSession(path1, updatedSession1, cache, cacheLimit: 2);

        Assert.Equal(2, cache.Count);
        Assert.Contains(key1, cache.Keys);
        Assert.Contains(key2, cache.Keys);

        // Verify the update was applied
        var loaded = _svc.LoadSessionForMedia(path1, cache);
        Assert.Equal(SessionWorkflowStage.Transcribed, loaded!.Stage);
    }

    [Fact]
    public void CacheCurrentSession_CacheLimitOfOne_AlwaysReplacesWithNewest()
    {
        var cache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);

        var path1 = Path.Combine(_dir, "limit1_media1.mp4");
        var path2 = Path.Combine(_dir, "limit1_media2.mp4");
        var key1 = Path.GetFullPath(path1);
        var key2 = Path.GetFullPath(path2);

        _svc.CacheCurrentSession(path1, MakeSession(mediaPath: path1), cache, cacheLimit: 1);
        Assert.Single(cache);
        Assert.Contains(key1, cache.Keys);

        _svc.CacheCurrentSession(path2, MakeSession(mediaPath: path2), cache, cacheLimit: 1);
        Assert.Single(cache);
        Assert.DoesNotContain(key1, cache.Keys);
        Assert.Contains(key2, cache.Keys);
    }

    [Fact]
    public void StashCurrentSession_WithConcurrentDictionary_UsesOrdinalIgnoreCaseKeys()
    {
        // Verify that the ConcurrentDictionary uses OrdinalIgnoreCase so paths
        // that differ only in case resolve to the same entry.
        var cache = new ConcurrentDictionary<string, WorkflowSessionSnapshot>(StringComparer.OrdinalIgnoreCase);
        var mediaPath = Path.Combine(_dir, "CaseSensitive.mp4");
        var session = MakeSession(mediaPath: mediaPath);

        _svc.StashCurrentSession(session, cache, cacheLimit: 5);

        // Look up with the same key — should find it regardless of case on OrdinalIgnoreCase dict
        var upperKey = Path.GetFullPath(mediaPath).ToUpperInvariant();
        var lowerKey = Path.GetFullPath(mediaPath).ToLowerInvariant();

        // At least one of the case variants should find the entry (OrdinalIgnoreCase)
        bool foundUpper = cache.ContainsKey(upperKey);
        bool foundLower = cache.ContainsKey(lowerKey);
        Assert.True(foundUpper || foundLower, "OrdinalIgnoreCase lookup should find the cached session by any case variant");
    }
}