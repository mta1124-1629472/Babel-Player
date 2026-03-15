using BabelPlayer.Core;

namespace BabelPlayer.App;

// ── DTOs & enums ────────────────────────────────────────────────────────────

public enum ShellMediaOpenTrigger { Manual, Autoplay }

public enum ShellResumeDecision { Resume, StartOver, Dismiss }

public sealed record ShellLoadMediaOptions
{
    public ShellHardwareDecodingMode HardwareDecodingMode { get; init; } = ShellHardwareDecodingMode.AutoSafe;
    public double PlaybackRate        { get; init; } = 1.0;
    public string AspectRatio         { get; init; } = "auto";
    public double AudioDelaySeconds   { get; init; }
    public double SubtitleDelaySeconds{ get; init; }
    public double Volume              { get; init; } = 0.8;
    public bool   IsMuted             { get; init; }
    public bool   ResumeEnabled       { get; init; }
    public ShellMediaOpenTrigger        OpenTrigger          { get; init; } = ShellMediaOpenTrigger.Manual;
    public ShellPlaybackStateSnapshot   PreviousPlaybackState{ get; init; } = new();
}

public sealed record ShellQueueMediaResult
{
    public IReadOnlyList<ShellPlaylistItem> AddedItems     { get; init; } = [];
    public ShellPlaylistItem?               ItemToLoad     { get; init; }
    public IReadOnlyList<string>            PinnedFolders  { get; init; } = [];
    public bool                             RevealBrowserPane { get; init; }
    public ShellPreferencesSnapshot?        UpdatedPreferences{ get; init; }
    public string?                          StatusMessage  { get; init; }
    public bool                             IsError        { get; init; }
}

public sealed record ShellQueueSnapshot
{
    public ShellPlaylistItem?               NowPlayingItem { get; init; }
    public IReadOnlyList<ShellPlaylistItem> QueueItems     { get; init; } = [];
    public IReadOnlyList<ShellPlaylistItem> HistoryItems   { get; init; } = [];
}

public sealed record ShellPlaybackOpenResult
{
    public ShellMediaOpenTrigger OpenTrigger         { get; init; } = ShellMediaOpenTrigger.Manual;
    public TimeSpan?             ResumePosition      { get; init; }
    public bool                  ResumeDecisionPending{ get; init; }
    public string                StatusMessage       { get; init; } = "Media opened.";
}

public sealed record ShellResumeDecisionResult
{
    public bool   DecisionApplied { get; init; }
    public string StatusMessage   { get; init; } = "No pending resume decision.";
}

public sealed record ShellMediaEndedResult
{
    public ShellPlaylistItem? NextItem      { get; init; }
    public string             StatusMessage { get; init; } = "Playback ended.";
}

public sealed record ShellWorkflowTransitionResult
{
    public string? StatusMessage      { get; init; }
    public bool?   StartupGateBlocking{ get; init; }
}

public sealed record ShellSubtitleTrackSelectionResult
{
    public int?   SelectedSubtitleTrackId{ get; init; }
    public bool   TrackSelectionChanged  { get; init; }
    public string StatusMessage          { get; init; } = string.Empty;
    public bool   IsError               { get; init; }
}

// ── Core partial class — fields, constructor, Dispose, queue projection ─────

public sealed partial class ShellController : IQueueProjectionReader, IQueueCommands, IShellPlaybackCommands, IDisposable
{
    private readonly PlaybackQueueController      _playbackQueueController;
    private readonly IPlaybackBackend             _playbackBackend;
    private readonly SubtitleWorkflowController   _subtitleWorkflowController;
    private readonly LibraryBrowserService        _libraryBrowserService;
    private readonly ResumePlaybackService        _resumePlaybackService;
    private readonly ResumeTrackingCoordinator    _resumeTrackingCoordinator;
    private readonly IShellPreferencesService     _shellPreferencesService;
    private readonly IBabelLogger                 _logger;

    // pending-state fields used across partials
    private PendingMediaOpenContext? _pendingMediaOpenContext;
    private PendingResumeDecision?   _pendingResumeDecision;

    private sealed record PendingMediaOpenContext(
        string Path, bool ResumeEnabled, ShellMediaOpenTrigger OpenTrigger);

    private sealed record PendingResumeDecision(
        string Path, TimeSpan ResumePosition);

    public event Action<ShellQueueSnapshot>? QueueSnapshotChanged;

    public ShellController(
        PlaybackQueueController      playbackQueueController,
        IPlaybackBackend             playbackBackend,
        SubtitleWorkflowController   subtitleWorkflowController,
        LibraryBrowserService        libraryBrowserService,
        ResumePlaybackService        resumePlaybackService,
        IShellPreferencesService     shellPreferencesService,
        IBabelLogFactory?            logFactory = null)
    {
        _playbackQueueController    = playbackQueueController;
        _playbackBackend            = playbackBackend;
        _subtitleWorkflowController = subtitleWorkflowController;
        _libraryBrowserService      = libraryBrowserService;
        _resumePlaybackService      = resumePlaybackService;
        _shellPreferencesService    = shellPreferencesService;
        _logger = (logFactory ?? NullBabelLogFactory.Instance).CreateLogger("shell.controller");
        _resumeTrackingCoordinator  = new ResumeTrackingCoordinator(playbackBackend, resumePlaybackService);
        _playbackQueueController.SnapshotChanged += HandleQueueSnapshotChanged;
    }

    public void Dispose()
    {
        _playbackQueueController.SnapshotChanged -= HandleQueueSnapshotChanged;
        _resumeTrackingCoordinator.Dispose();
    }

    // ── IQueueProjectionReader ───────────────────────────────────────────────

    public ShellQueueSnapshot QueueSnapshot =>
        MapQueueSnapshot(_playbackQueueController.Snapshot);

    public ShellPlaylistItem? NowPlayingItem =>
        _playbackQueueController.NowPlayingItem?.ToShell();

    public IReadOnlyList<PlaylistItem> QueueItems =>
        _playbackQueueController.QueueItems;

    public IReadOnlyList<PlaylistItem> HistoryItems =>
        _playbackQueueController.HistoryItems;

    public ShellPlaybackStateSnapshot CurrentPlaybackSnapshot =>
        (_resumeTrackingCoordinator.CurrentSnapshot with
        {
            PlaylistIndex = _playbackQueueController.NowPlayingItem is null ? -1 : 0,
            PlaylistCount = _playbackQueueController.QueueItems.Count
        }).ToShell();

    // ── Internal helpers shared across partials ──────────────────────────────

    private void HandleQueueSnapshotChanged(PlaybackQueueSnapshot snapshot)
    {
        _logger.LogInfo("Queue snapshot changed.",
            BabelLogContext.Create(
                ("nowPlaying",    snapshot.NowPlayingItem?.DisplayName),
                ("upNextCount",   snapshot.QueueItems.Count),
                ("historyCount",  snapshot.HistoryItems.Count)));
        QueueSnapshotChanged?.Invoke(MapQueueSnapshot(snapshot));
    }

    private static ShellQueueSnapshot MapQueueSnapshot(PlaybackQueueSnapshot snapshot) => new()
    {
        NowPlayingItem = snapshot.NowPlayingItem?.ToShell(),
        QueueItems     = snapshot.QueueItems.Select(i => i.ToShell()).ToArray(),
        HistoryItems   = snapshot.HistoryItems.Select(i => i.ToShell()).ToArray()
    };

    private ShellPreferencesSnapshot RevealBrowserPanePreference()
    {
        var current = _shellPreferencesService.Current;
        if (current.ShowBrowserPanel) return current;
        return _shellPreferencesService.ApplyLayoutChange(new ShellLayoutPreferencesChange(
            true, current.ShowPlaylistPanel, current.WindowMode));
    }

    private void SetPendingMediaOpenContext(string path, bool resumeEnabled, ShellMediaOpenTrigger trigger)
        => _pendingMediaOpenContext = new PendingMediaOpenContext(path, resumeEnabled, trigger);

    private PendingMediaOpenContext? ConsumePendingMediaOpenContext(string? path)
    {
        var ctx = _pendingMediaOpenContext;
        if (ctx is null) return null;
        if (!string.IsNullOrWhiteSpace(path) &&
            string.Equals(ctx.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            _pendingMediaOpenContext = null;
            return ctx;
        }
        if (!string.IsNullOrWhiteSpace(path))
            _pendingMediaOpenContext = null;
        return null;
    }

    private void ClearPendingMediaOpenContext(string path)
    {
        if (_pendingMediaOpenContext is not null &&
            string.Equals(_pendingMediaOpenContext.Path, path, StringComparison.OrdinalIgnoreCase))
            _pendingMediaOpenContext = null;
    }

    private void SetPendingResumeDecision(string path, TimeSpan position)
        => _pendingResumeDecision = new PendingResumeDecision(path, position);

    private void ClearPendingResumeDecision()
        => _pendingResumeDecision = null;
}
