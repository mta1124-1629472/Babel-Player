using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Babel.Player.Models;

namespace Babel.Player.Services;

/// <summary>
/// Owns the persistence/cache mechanics around switching between workflow
/// sessions so the coordinator can focus on stage meaning instead of storage churn.
/// </summary>
public sealed class SessionSwitchService
{
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;
    private readonly AppLog _log;
    private readonly object _cacheOrderLock = new();
    private readonly Queue<string> _cacheInsertionOrder = new();

    /// <summary>
    /// Initializes a new instance of <see cref="SessionSwitchService"/> with the specified stores and logger.
    /// </summary>
    /// <param name="perSessionStore">Provider for loading, saving, and resolving filesystem directories for session snapshots.</param>
    /// <param name="recentStore">Store for loading and upserting recent session entries.</param>
    /// <param name="log">Application logger used for informational messages such as cache eviction events.</param>
    public SessionSwitchService(
        PerSessionSnapshotStore perSessionStore,
        RecentSessionsStore recentStore,
        AppLog log)
    {
        _perSessionStore = perSessionStore;
        _recentStore = recentStore;
        _log = log;
    }

    /// <summary>
    /// Persists and caches the provided session and updates the recent-sessions list.
    /// </summary>
    /// <remarks>
    /// If <paramref name="currentSession"/> has a null or whitespace <c>SourceMediaPath</c>, the recent-sessions list is returned unchanged.
    /// Otherwise the method caches the session under the media key, saves the session to persistent storage, upserts a recent-session entry, and then returns the updated recent list.
    /// </remarks>
    /// <param name="currentSession">The session snapshot to persist and cache.</param>
    /// <param name="mediaSnapshotCache">The concurrent cache in which the session snapshot is stored by media key.</param>
    /// <param name="cacheLimit">Maximum number of entries allowed in <paramref name="mediaSnapshotCache"/>; exceeding this may evict the oldest cached entry.</param>
    /// <summary>
    /// Stashes the provided session snapshot: updates the in-memory media cache, persists the snapshot, and updates the recent-sessions list while enforcing the cache size limit.
    /// </summary>
    /// <param name="currentSession">The session snapshot to stash and persist.</param>
    /// <param name="mediaSnapshotCache">The concurrent media-keyed cache to update with the snapshot.</param>
    /// <param name="cacheLimit">Maximum number of entries to retain in <paramref name="mediaSnapshotCache"/>; oldest entries will be evicted when exceeded.</param>
    /// <returns>The recent sessions list after applying the upsert for the provided session.</returns>
    public IReadOnlyList<RecentSessionEntry> StashCurrentSession(
        WorkflowSessionSnapshot currentSession,
        ConcurrentDictionary<string, WorkflowSessionSnapshot> mediaSnapshotCache,
        int cacheLimit)
    {
        if (string.IsNullOrWhiteSpace(currentSession.SourceMediaPath))
            return _recentStore.Load();

        CacheMediaSnapshot(
            mediaSnapshotCache,
            SessionWorkflowCoordinator.MediaKey(currentSession.SourceMediaPath),
            currentSession,
            cacheLimit);
        _perSessionStore.Save(currentSession);
        _recentStore.Upsert(new RecentSessionEntry(
            currentSession.SessionId,
            currentSession.SourceMediaPath,
            Path.GetFileName(currentSession.SourceMediaPath),
            currentSession.Stage,
            currentSession.LastUpdatedAtUtc));
        return _recentStore.Load();
    }

    public WorkflowSessionSnapshot? LoadSession(
        Guid sessionId,
        IReadOnlyDictionary<string, WorkflowSessionSnapshot> mediaSnapshotCache) =>
        mediaSnapshotCache.Values.FirstOrDefault(snapshot => snapshot.SessionId == sessionId)
        ?? _perSessionStore.Load(sessionId);

    /// <summary>
    /// Retrieves the cached session snapshot associated with the specified media path.
    /// </summary>
    /// <param name="mediaPath">The media file path used to derive the cache key.</param>
    /// <param name="mediaSnapshotCache">A read-only map of media keys to session snapshots.</param>
    /// <returns>The cached <see cref="WorkflowSessionSnapshot"/> for the media path, or <c>null</c> if none is present.</returns>
    public WorkflowSessionSnapshot? LoadSessionForMedia(
        string mediaPath,
        IReadOnlyDictionary<string, WorkflowSessionSnapshot> mediaSnapshotCache)
    {
        mediaSnapshotCache.TryGetValue(
            SessionWorkflowCoordinator.MediaKey(mediaPath),
            out var snapshot);
        return snapshot;
    }

    /// <summary>
    /// Stores the given session snapshot in the media cache under the key derived from the provided media path and enforces the cache size limit.
    /// </summary>
    /// <param name="mediaPath">File system path or identifier for the media used to derive the cache key.</param>
    /// <param name="snapshot">The session snapshot to store in the cache.</param>
    /// <param name="mediaSnapshotCache">The concurrent cache mapping media keys to session snapshots.</param>
    /// <summary>
    /// Adds or updates the provided session snapshot in the media-keyed cache and enforces the cache size limit.
    /// </summary>
    /// <param name="mediaPath">Path to the source media used to derive the cache key.</param>
    /// <param name="snapshot">The session snapshot to store in the cache.</param>
    /// <param name="mediaSnapshotCache">The concurrent cache mapping media keys to session snapshots.</param>
    /// <param name="cacheLimit">Maximum number of entries to retain in the cache; when exceeded, the oldest entry is evicted.</param>
    public void CacheCurrentSession(
        string mediaPath,
        WorkflowSessionSnapshot snapshot,
        ConcurrentDictionary<string, WorkflowSessionSnapshot> mediaSnapshotCache,
        int cacheLimit)
    {
        CacheMediaSnapshot(
            mediaSnapshotCache,
            SessionWorkflowCoordinator.MediaKey(mediaPath),
            snapshot,
            cacheLimit);
    }

    /// <summary>
    /// Get the filesystem directory path where the specified session's data is stored.
    /// </summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <returns>The directory path for the session.</returns>
    public string GetSessionDirectory(Guid sessionId) =>
        _perSessionStore.GetSessionDirectory(sessionId);

    /// <summary>
    /// Adds or updates the snapshot for the given media key in the provided cache and, if the cache size exceeds the specified limit, evicts the oldest entry.
    /// </summary>
    /// <param name="cache">The concurrent media snapshot cache keyed by media key.</param>
    /// <param name="key">The media lookup key under which to store the snapshot.</param>
    /// <param name="snapshot">The session snapshot to store in the cache.</param>
    /// <summary>
    /// Adds or updates a workflow session snapshot in the media cache and enforces a bounded size by evicting the oldest entries.
    /// </summary>
    /// <param name="cache">The concurrent cache mapping media keys to workflow session snapshots.</param>
    /// <param name="key">The media-derived key under which to store the snapshot.</param>
    /// <param name="snapshot">The session snapshot to add or update in the cache.</param>
    /// <param name="cacheLimit">Maximum number of entries to keep in the cache; when the cache size becomes greater than this value, the oldest entry is removed based on insertion order.</param>
    private void CacheMediaSnapshot(
        ConcurrentDictionary<string, WorkflowSessionSnapshot> cache,
        string key,
        WorkflowSessionSnapshot snapshot,
        int cacheLimit)
    {
        lock (_cacheOrderLock)
        {
            bool isNew = !cache.ContainsKey(key);
            cache[key] = snapshot;
            
            if (isNew)
            {
                _cacheInsertionOrder.Enqueue(key);
            }
            
            // Evict oldest entries if we exceed the limit
            while (cache.Count > cacheLimit && _cacheInsertionOrder.Count > 0)
            {
                var oldestKey = _cacheInsertionOrder.Dequeue();
                if (cache.TryRemove(oldestKey, out _))
                {
                    _log.Info($"Evicted cached session for media key '{oldestKey}' to keep cache bounded.");
                }
            }
        }
    }
}