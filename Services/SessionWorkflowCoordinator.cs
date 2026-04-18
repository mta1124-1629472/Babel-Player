using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Subjects;
using Babel.Player.Models;
using Babel.Player.Services.Orchestration;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Planning;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CoordinatorCoreServices = Babel.Player.Models.CoordinatorCoreServices;
using CoordinatorOptions = Babel.Player.Models.CoordinatorOptions;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator : ObservableObject, IDisposable
{
    private readonly SessionSnapshotStore _store;
    private readonly AppLog _log;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;
    private readonly SessionArtifactReader _artifactReader;
    private readonly SessionSwitchService _sessionSwitchService;
    private readonly ContainerizedServiceProbe? _containerizedProbe;
    private readonly IContainerizedInferenceManager? _containerizedInferenceManager;
    private readonly ManagedCpuRuntimeManager _cpuRuntimeManager;
    public ITranscriptionRegistry TranscriptionRegistry { get; }
    public ITranslationRegistry TranslationRegistry { get; }
    public ITtsRegistry TtsRegistry { get; }
    public IDiarizationRegistry? DiarizationRegistry { get; private set; }
    private ITranscriptionProvider? _transcriptionService;
    private ITranslationProvider? _translationService;
    private ITtsProvider? _ttsService;
    private IVocalSeparationProvider? _vocalSeparationProvider;
    private readonly ContainerizedRequestLeaseTracker? _requestLeaseTracker;
    private readonly List<Task> _pendingTtsTasks = [];
    private readonly object _pendingTtsTasksLock = new();
    private readonly IAudioProcessingService? _audioProcessingService;
    private readonly object _sessionLock = new();


    private readonly IInferenceExecutionEngine _inferenceEngine;
    private readonly IExecutionPlanner _executionPlanner;
    private readonly TranscriptionOrchestrator _transcriptionOrchestrator;
    private readonly TranslationOrchestrator _translationOrchestrator;
    private readonly DiarizationStageOrchestrator _diarizationStageOrchestrator;
    private readonly TtsPipelineOrchestrator _ttsPipelineOrchestrator;
    private readonly StreamingPipelineOrchestrator _streamingPipelineOrchestrator;
    private readonly IMediaTransportManager _transportManager;
    private bool _subscribedToSegmentEvents;
    private bool _subscribedToSourceDiagnostics;
    private readonly EventHandler _segmentEndedHandler;
    private readonly EventHandler<Exception> _segmentErrorHandler;
    /// <summary>When pause-mode TTS is waiting on segment Ended, <see cref="StopTtsPlayback"/> completes this so the wait does not hang.</summary>
    private TaskCompletionSource<bool>? _ttsPauseModeCompletion;
    private readonly Action<VsrDiagnosticSnapshot> _vsrDiagnosticChangedHandler;
    private VsrDiagnosticSnapshot? _latestVsrDiagnostic;
    private readonly ConcurrentDictionary<string, WorkflowSessionSnapshot> _mediaSnapshotCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Subject<ReadinessSignal> _readinessSignals = new();
    private readonly ConcurrentDictionary<string, ContainerizedProbeState> _lastProbeStates =
        new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private WorkflowSessionSnapshot _currentSession = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);

    [ObservableProperty]
    private string _sessionSource = "Session not initialized.";

    [ObservableProperty]
    private string _persistenceStatus = "Persistence has not run yet.";

    [ObservableProperty]
    private string? _activeTtsSegmentId;

    [ObservableProperty]
    private PlaybackState _playbackState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentSessions))]
    private IReadOnlyList<RecentSessionEntry> _recentSessions = [];

    [ObservableProperty]
    private BootstrapDiagnostics _bootstrapDiagnostics = new(false, null, false, null, false, null, false, false, null, null, "Detecting...");

    [ObservableProperty]
    private HardwareSnapshot _hardwareSnapshot = HardwareSnapshot.Detecting;

    private VideoEnhancementDiagnostics _videoEnhancementDiagnostics = VideoEnhancementDiagnostics.Initial;

    [ObservableProperty]
    private InferenceMode _inferenceMode = InferenceMode.SubprocessCpu;

    [ObservableProperty]
    private MediaReloadRequest? _pendingMediaReloadRequest;

    [ObservableProperty]
    private double _ttsPlaybackRate = 1.0;

    [ObservableProperty]
    private double _ttsVolume = 1.0;

    [ObservableProperty]
    private string? _runtimeWarmupStatusText;

    [ObservableProperty]
    private DateTimeOffset _readinessLastUpdatedUtc;

    [ObservableProperty]
    private ReadinessSignal? _lastReadinessSignal;

    /// <summary>
    /// Set when the CTranslate2 translation provider fails and the pipeline
    /// automatically falls back to the NLLB PyTorch provider.
    /// Null when no fallback has occurred. Exposed so the UI can show a note
    /// in the Active Config panel (e.g. "NMT: CTranslate2 → NLLB fallback").
    /// </summary>
    [ObservableProperty]
    private string? _translationFallbackNote;

    public bool HasRecentSessions => RecentSessions.Count > 0;

    public AppSettings CurrentSettings { get; private set; }

    /// <summary>UTC time when this coordinator instance was created — used for cold-start UX (warm-up hints).</summary>
    public DateTimeOffset ProcessStartedAtUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Raised when AppSettings are modified in-place (e.g. by left-panel dropdowns).
    /// Subscribers should call SettingsService.Save() in response.
    /// </summary>
    public event Action? SettingsModified;
    public IObservable<ReadinessSignal> ReadinessSignals => _readinessSignals;

    public ApiKeyStore? KeyStore { get; private set; }

    /// <summary>
    /// Initializes a new <see cref="SessionWorkflowCoordinator"/> with the provided core services, transport manager, and registries, and prepares internal orchestration, runtime, and probe wiring required to manage the session workflow.
    /// </summary>
    /// <param name="coreServices">Core application services and stores required by the coordinator (settings, persistence store, logging).</param>
    /// <param name="transportManager">Media transport manager used for playback and segment transport.</param>
    /// <param name="registries">Provider registries and per-session/recent session stores used to resolve transcription, translation, TTS, and persistence backends.</param>
    /// <param name="options">Optional coordinator extensions and test hooks (container probe/manager, audio processing, artifact reader, session switch service, key store, diarization registry). When null, sensible defaults are applied.</param>
    public SessionWorkflowCoordinator(
        CoordinatorCoreServices coreServices,
        IMediaTransportManager transportManager,
        RegistryBundle registries,
        CoordinatorOptions? options = null)
    {
        options ??= CoordinatorOptions.Empty;

        _store = coreServices.Store;
        _log = coreServices.Log;
        _perSessionStore = registries.PerSessionStore;
        _recentStore = registries.RecentStore;
        _containerizedProbe = options.ContainerizedProbe;
        _containerizedInferenceManager = options.ContainerizedInferenceManager;
        _audioProcessingService = options.AudioProcessingService;
        _artifactReader = options.ArtifactReader ?? new SessionArtifactReader();
        _sessionSwitchService = options.SessionSwitchService
            ?? new SessionSwitchService(registries.PerSessionStore, registries.RecentStore, _log);

        _cpuRuntimeManager = new ManagedCpuRuntimeManager(_log);
        TranscriptionRegistry = registries.TranscriptionRegistry;
        TranslationRegistry = registries.TranslationRegistry;
        TtsRegistry = registries.TtsRegistry;
        DiarizationRegistry = options.DiarizationRegistry;
        CurrentSettings = coreServices.Settings;
        KeyStore = options.KeyStore;
        _transportManager = transportManager;
        _inferenceEngine = options.InferenceExecutionEngine ?? DefaultInferenceExecutionEngine.Instance;
        _executionPlanner = options.ExecutionPlanner ?? DefaultExecutionPlanner.Instance;
        _requestLeaseTracker = options.RequestLeaseTracker;
        _transcriptionOrchestrator = new TranscriptionOrchestrator(this, this, this, this, _inferenceEngine, _log);
        _translationOrchestrator = new TranslationOrchestrator(this, this, this, this, _inferenceEngine, _log);
        _diarizationStageOrchestrator = new DiarizationStageOrchestrator(this, this, this, _log);
        _ttsPipelineOrchestrator = new TtsPipelineOrchestrator(this);
        _streamingPipelineOrchestrator = new StreamingPipelineOrchestrator(this);

        _segmentEndedHandler = OnSegmentPlayerEnded;
        _segmentErrorHandler = (_, _) => OnSegmentPlayerError();
        _vsrDiagnosticChangedHandler = RecordVsrDiagnosticSnapshot;
        if (_containerizedProbe is not null)
            _containerizedProbe.ProbeResultUpdated += OnProbeResultUpdated;

        RefreshVideoEnhancementDiagnostics();
    }

    /// <summary>
    /// Creates a <see cref="SessionWorkflowCoordinator"/> without a pre-built transport manager,
    /// constructing a default <see cref="MediaTransportManager"/> from optional segment/source players.
    /// Convenience overload for tests and minimal-host scenarios.
    /// </summary>
    public SessionWorkflowCoordinator(
        CoordinatorCoreServices coreServices,
        RegistryBundle registries,
        CoordinatorOptions? options = null,
        IMediaTransport? segmentPlayer = null,
        IMediaTransport? sourcePlayer = null)
        : this(
            coreServices,
            new MediaTransportManager(
                segmentPlayer,
                sourcePlayer,
                videoOptionsFactory: () => new VideoPlaybackOptions(
                    HwdecMode:           coreServices.Settings.VideoHwdec,
                    GpuApi:              coreServices.Settings.VideoGpuApi,
                    UseGpuNext:          coreServices.Settings.VideoUseGpuNext,
                    VsrEnabled:          coreServices.Settings.VideoVsrEnabled,
                    HdrPlaybackMode:     coreServices.Settings.VideoHdrPlaybackMode,
                    AllowHdrPassthrough: coreServices.Settings.VideoHdrPlaybackMode != VideoHdrPlaybackMode.Off
                        && HardwareSnapshot.QueryActiveHdrDisplay(),
                    ToneMapping:         coreServices.Settings.VideoToneMapping,
                    TargetPeak:          coreServices.Settings.VideoTargetPeak,
                    HdrComputePeak:      coreServices.Settings.VideoHdrComputePeak),
                log: coreServices.Log),
            registries,
            options)
    {
    }

    public string StateFilePath => _store.StateFilePath;

    /// <summary>
    /// Handles the segment-player "ended" event by either completing a pending TTS pause wait or stopping TTS playback.
    /// </summary>
    /// <remarks>
    /// Expected state on entry: invoked when a media segment playback finishes. If a pause-mode TTS await is active (represented by <c>_ttsPauseModeCompletion</c>), this method completes that <c>TaskCompletionSource&lt;bool&gt;</c> with <c>true</c>, causing awaiting code to resume. If no pause-mode await is active, the method stops TTS playback by calling <c>StopTtsPlayback()</c>. This method does not persist session state and completes synchronously. It does not throw on normal operation.
    /// </remarks>
    private void OnSegmentPlayerEnded(object? sender, EventArgs e)
    {
        if (_ttsPauseModeCompletion is null)
        {
            StopTtsPlayback();
            return;
        }

        _ttsPauseModeCompletion.TrySetResult(true);
    }

    /// <summary>
    /// Handles a playback error for the currently playing segment by either stopping TTS playback or signaling a pending TTS pause-mode wait with failure.
    /// </summary>
    /// <remarks>
    /// If a pause-mode TTS wait is active (indicated by <c>_ttsPauseModeCompletion</c>), the method completes that <see cref="TaskCompletionSource{bool}"/> with <c>false</c> to indicate an error. If no pause-mode wait is active, the method stops TTS playback via <see cref="StopTtsPlayback()"/>.
    /// This method is an event handler invoked when a segment player reports an error; it does not persist session state.
    /// </remarks>
    private void OnSegmentPlayerError()
    {
        if (_ttsPauseModeCompletion is null)
        {
            StopTtsPlayback();
            return;
        }

        _ttsPauseModeCompletion.TrySetResult(false);
    }

    public string LogFilePath => _log.LogFilePath;
    internal AppLog Log => _log;
    internal ContainerizedServiceProbe? ContainerizedProbe => _containerizedProbe;
    public IContainerizedInferenceManager? ContainerizedInferenceManager => _containerizedInferenceManager;
    internal VideoEnhancementDiagnostics VideoEnhancementDiagnostics
    {
        get => _videoEnhancementDiagnostics;
        private set => SetProperty(ref _videoEnhancementDiagnostics, value);
    }

    /// <summary>
    /// Bootstraps coordinator state by loading a persisted session snapshot or creating a new foundation session when none exists.
    /// </summary>
    /// <remarks>
    /// On entry: may be called at application startup; no specific pipeline stage is required.
    /// On success: restores CurrentSession to the persisted snapshot (possibly with its stage downgraded if artifacts are missing) or sets a newly created foundation session; updates persistence-related properties and recent-session list.
    /// Side effects: caches the session snapshot for the media key when applicable, enqueues a media reload request if the restored session is at or past MediaLoaded, and persists the current session to disk.
    /// Cancellation: this method is synchronous and does not support cancellation.
    /// <summary>
    /// Bootstraps coordinator state from persistent stores and prepares session-related caches and readiness signals.
    /// </summary>
    /// <remarks>
    /// Entry state: none required; can be called during startup to initialize coordinator internals.  
    /// On success: sets <c>CurrentSession</c> to either a restored and validated snapshot or a new foundation session, updates <c>BootstrapDiagnostics</c>, <c>RecentSessions</c>, and <c>SessionSource</c>. If the restored snapshot contained media, the snapshot is cached and a media reload is queued.  
    /// Persistence: always persists the chosen <c>CurrentSession</c> by calling <c>SaveCurrentSession()</c>.  
    /// Side effects: may log artifact validation/downgrade warnings, update <c>PersistenceStatus</c>, and enqueue a media reload request when the session stage is at or beyond <c>SessionWorkflowStage.MediaLoaded</c>.  
    /// Cancellation: synchronous and non-cancelable.
    /// </remarks>
    public void Initialize()
    {
        // Heavy bootstrap probes and per-session snapshot preloading are warmed in background.
        BootstrapDiagnostics = new BootstrapDiagnostics(false, null, false, null, false, null, false, false, null, null, "Detecting...");

        var nowUtc = DateTimeOffset.UtcNow;
        var loadResult = _store.Load();

        if (loadResult.Snapshot is null)
        {
            lock (_sessionLock)
            {
                CurrentSession = WorkflowSessionSnapshot.CreateNew(nowUtc);
            }
            SessionSource = "Created a new foundation session.";
        }
        else
        {
            var snapshot = loadResult.Snapshot;
            var validation = SessionSnapshotSemantics.ValidateArtifacts(snapshot);
            var validated = validation.Snapshot;

            // Log any artifacts that were dropped by validation
            if (snapshot.Stage != validated.Stage)
                _log.Warning($"Session stage downgraded on load: {snapshot.Stage} → {validated.Stage} (missing artifacts)");
            if (validation.OriginalStage != validated.Stage)
            {
                _log.Warning(
                    $"ValidateArtifacts[startup-load]: downgraded stage {validation.OriginalStage} -> {validated.Stage}; " +
                    $"cleared={string.Join(",", validation.ClearedArtifacts)}; provenance={SessionSnapshotSemantics.DescribeSessionProvenance(validated)}");
            }

            const string statusMessage = "Ready.";

            lock (_sessionLock)
            {
                CurrentSession = validated with
                {
                    LastUpdatedAtUtc = nowUtc,
                    StatusMessage = statusMessage,
                };
            }

            // Primary current-session.json is authoritative — overwrite per-session cache entry.
            if (!string.IsNullOrEmpty(CurrentSession.SourceMediaPath))
                CacheMediaSnapshot(MediaKey(CurrentSession.SourceMediaPath), CurrentSession);

            SessionSource = validated.Stage != snapshot.Stage
                ? $"Resumed session (stage downgraded from {snapshot.Stage} to {validated.Stage}: missing artifacts)."
                : validated.Stage >= SessionWorkflowStage.TtsGenerated
                ? "Resumed session with TTS."
                : validated.Stage >= SessionWorkflowStage.Translated
                    ? "Resumed session with translation."
                    : validated.Stage >= SessionWorkflowStage.Diarized
                        ? "Resumed session with speaker mapping."
                        : validated.Stage >= SessionWorkflowStage.Transcribed
                            ? "Resumed session with transcript."
                            : "Resumed the saved foundation session.";
        }

        PersistenceStatus = loadResult.StatusMessage;
        RecentSessions = _recentStore.Load();
        _log.Info(SessionSource);
        if (CurrentSession.Stage >= SessionWorkflowStage.MediaLoaded)
            QueueMediaReloadRequest(autoPlay: false, "initialize");
        SaveCurrentSession();
    }

    public BootstrapWarmupData GatherBootstrapWarmupData()
    {
        var diagnostics = BootstrapDiagnostics.Run(CurrentSettings.EffectiveGpuServiceUrl);
        var snapshots = _perSessionStore.LoadAll();
        var inferenceMode = ResolveInferenceMode(diagnostics);
        return new BootstrapWarmupData(diagnostics, snapshots, inferenceMode);
    }

    public void ApplyBootstrapWarmupData(BootstrapWarmupData warmup)
    {
        BootstrapDiagnostics = warmup.Diagnostics;
        InferenceMode = warmup.ResolvedInferenceMode;
        EmitReadinessSignal(
            ReadinessSignalKind.BootstrapApplied,
            summary: "Bootstrap diagnostics updated.",
            source: nameof(ApplyBootstrapWarmupData),
            forceRefresh: true);

        if (!BootstrapDiagnostics.AllDependenciesAvailable)
            _log.Warning($"Bootstrap: {BootstrapDiagnostics.DiagnosticSummary}");
        else
            _log.Info("Bootstrap: all dependencies available.");

        _log.Info($"Bootstrap: inference mode = {InferenceMode} ({BootstrapDiagnostics.InferenceLine})");

        foreach (var snapshot in warmup.Snapshots)
        {
            if (!string.IsNullOrEmpty(snapshot.SourceMediaPath))
                CacheMediaSnapshot(MediaKey(snapshot.SourceMediaPath), snapshot);
        }
    }

    /// <summary>
    /// Determines the inference execution mode based on container availability and the configured GPU service URL.
    /// </summary>
    /// <param name="diagnostics">Bootstrap diagnostics indicating containerized service availability and its base URL.</param>
    /// <returns>
    /// `InferenceMode.ManagedVenv` if a containerized service is available and its URL equals the configured managed GPU service URL (case-insensitive);
    /// `InferenceMode.Containerized` if a containerized service is available but the URL differs;
    /// otherwise `InferenceMode.SubprocessCpu`.
    /// </returns>
    private static InferenceMode ResolveInferenceMode(BootstrapDiagnostics diagnostics)
    {
        if (diagnostics.ContainerizedServiceAvailable)
        {
            return string.Equals(
                diagnostics.ContainerizedServiceUrl,
                AppSettings.ManagedGpuServiceUrl,
                StringComparison.OrdinalIgnoreCase)
                ? InferenceMode.ManagedVenv
                : InferenceMode.Containerized;
        }

        return InferenceMode.SubprocessCpu;
    }

    /// <summary>
    /// Loads the specified media file into the coordinator, restoring a cached session if available or creating a new session for the media.
    /// </summary>
    /// <param name="sourceMediaPath">Absolute or relative path to the source media file to ingest.</param>
    /// <remarks>
    /// Entry state: may be called at any time; if a current session has a SourceMediaPath, the coordinator treats this as a media switch.
    /// Exit state: on success the coordinator's CurrentSession will be at least <see cref="SessionWorkflowStage.MediaLoaded"/> and its media/artifact paths and status message will reflect either the restored snapshot or a newly created session; a media reload request is queued.
    /// Persistence: when switching media the existing session is stashed into the MRU/per-session store and the new/restored session is persisted (FlushPendingSave is invoked) so state survives restarts.
    /// Behavior: if a previously cached snapshot exists for the supplied media the method validates and restores that snapshot (copying the media into the snapshot's session directory) and retains restored artifact paths; otherwise it creates a new per-session directory, copies the media there, resets downstream artifacts, and sets a fresh SessionId when switching media.
    /// Cancellation: this method is synchronous and does not support cancellation.
    /// </remarks>
    /// <summary>
    /// Loads and ingests a media file into the coordinator, creating a new per-session identity or restoring a cached session for that media and preparing the pipeline for media-loaded state.
    /// </summary>
    /// <param name="sourceMediaPath">Filesystem path of the source media file to ingest; the file must exist.</param>
    /// <remarks>
    /// Expected entry state: can be called at any time after coordinator initialization; if a current session already references media, that session will be stashed before switching. 
    /// On success: the coordinator's CurrentSession will be set to either a restored snapshot (preserving its stage) or to Stage = MediaLoaded for a newly created session, and media artifacts will be copied into the session's storage directory. 
    /// Persistence: the updated session state is enqueued and flushed to persistent storage before the method returns. 
    /// Cancellation: this synchronous method does not support cancellation.
    /// </remarks>
    /// <exception cref="FileNotFoundException">Thrown when the file at <paramref name="sourceMediaPath"/> does not exist.</exception>
    public void LoadMedia(string sourceMediaPath)
    {
        if (!File.Exists(sourceMediaPath))
            throw new FileNotFoundException($"Source media file not found: {sourceMediaPath}");

        var nowUtc = DateTimeOffset.UtcNow;

        // Stash current snapshot before switching — persist to disk so it survives restart.
        if (!string.IsNullOrEmpty(CurrentSession.SourceMediaPath))
        {
            RecentSessions = _sessionSwitchService.StashCurrentSession(
                CurrentSession,
                _mediaSnapshotCache,
                MediaSnapshotCacheLimit);
        }

        var newKey = MediaKey(sourceMediaPath);
        var switchingMedia = !string.IsNullOrEmpty(CurrentSession.SourceMediaPath)
            && !string.Equals(MediaKey(CurrentSession.SourceMediaPath), newKey,
                              StringComparison.OrdinalIgnoreCase);

        var cached = switchingMedia
            ? _sessionSwitchService.LoadSessionForMedia(sourceMediaPath, _mediaSnapshotCache)
            : null;
        if (cached is not null)
        {
            // Returning to a previously processed media — restore, validate, then copy into
            // that session's existing directory.
            var validation = SessionSnapshotSemantics.ValidateArtifacts(cached);
            var validated = validation.Snapshot;
            if (validation.OriginalStage != validated.Stage)
            {
                _log.Warning(
                    $"ValidateArtifacts[media-cache-restore]: downgraded stage {validation.OriginalStage} -> {validated.Stage}; " +
                    $"cleared={string.Join(",", validation.ClearedArtifacts)}; provenance={SessionSnapshotSemantics.DescribeSessionProvenance(validated)}");
            }

            var sessionDir = _sessionSwitchService.GetSessionDirectory(validated.SessionId);
            var mediaDir = Path.Combine(sessionDir, "media");
            Directory.CreateDirectory(mediaDir);
            var ingestedPath = Path.Combine(mediaDir, Path.GetFileName(sourceMediaPath));
            File.Copy(sourceMediaPath, ingestedPath, overwrite: true);
            _log.Info($"Copied media to session artifact: {ingestedPath}");

            lock (_sessionLock)
            {
                CurrentSession = validated with
                {
                    IngestedMediaPath = ingestedPath,
                    VocalsAudioPath = validated.VocalsAudioPath,
                    AmbianceAudioPath = validated.AmbianceAudioPath,
                    MediaLoadedAtUtc = nowUtc,
                    LastUpdatedAtUtc = nowUtc,
                    StatusMessage = validated.Stage >= SessionWorkflowStage.TtsGenerated
                        ? "Restored prior TTS. Ready for playback."
                        : validated.Stage >= SessionWorkflowStage.Translated
                            ? "Restored translation. Ready for TTS/dubbing."
                        : validated.Stage >= SessionWorkflowStage.Diarized
                            ? "Restored speaker mapping state. Ready to resume translation/TTS."
                        : validated.Stage >= SessionWorkflowStage.Transcribed
                            ? "Restored transcript. Ready for translation."
                        : "Media loaded. Ready for transcription.",
                };
            }
            _log.Info($"Restored cached session for: {sourceMediaPath} (stage: {CurrentSession.Stage})");
        }
        else
        {
            // New uncached media — assign a fresh session ID so each media file gets its own
            // identity in the MRU list and per-session store. Re-use the current session ID
            // only when loading the first media (no prior source) so the coordinator's initial
            // session is promoted rather than orphaned.
            var newSessionId = switchingMedia ? Guid.NewGuid() : CurrentSession.SessionId;

            var sessionDir = _sessionSwitchService.GetSessionDirectory(newSessionId);
            var mediaDir = Path.Combine(sessionDir, "media");
            Directory.CreateDirectory(mediaDir);
            var ingestedPath = Path.Combine(mediaDir, Path.GetFileName(sourceMediaPath));
            File.Copy(sourceMediaPath, ingestedPath, overwrite: true);
            _log.Info($"Copied media to session artifact: {ingestedPath}");

            lock (_sessionLock)
            {
                CurrentSession = CreateMediaLoadedSession(
                    newSessionId,
                    sourceMediaPath,
                    ingestedPath,
                    nowUtc);
            }
        }

        QueueMediaReloadRequest(autoPlay: false, "media-switch");
        FlushPendingSave();
    }

    /// <summary>
/// Produce the absolute filesystem path for a media file.
/// </summary>
/// <param name="path">A relative or absolute path to the media file.</param>
/// <returns>The absolute path corresponding to the provided path.</returns>
internal static string MediaKey(string path) => Path.GetFullPath(path);

    private const int MediaSnapshotCacheLimit = 20;

    /// <summary>
    /// Adds or updates a snapshot in the media cache, evicting the oldest entry
    /// when the cache exceeds <see cref="MediaSnapshotCacheLimit"/> to prevent unbounded growth.
    /// </summary>
    private void CacheMediaSnapshot(string key, WorkflowSessionSnapshot snapshot)
    {
        _sessionSwitchService.CacheCurrentSession(
            key,
            snapshot,
            _mediaSnapshotCache,
            MediaSnapshotCacheLimit);
    }

    /// <summary>
    /// Injects a test transcript (and optional translation) into the current session, advancing the pipeline stage accordingly.
    /// </summary>
    /// <remarks>
    /// Expected entry state: can be called at any pipeline stage; it will replace transcript/translation artifacts for the active session.
    /// On success: the session's <c>Stage</c> is set to <c>Transcribed</c> when only a transcript is provided or <c>Translated</c> when a translation path is provided, and the session status message is updated.
    /// Persistence: the updated session is saved to persistent storage.
    /// Cancellation: this synchronous method does not support cancellation.
    /// </remarks>
    /// <param name="transcriptPath">Path to the transcript file to inject.</param>
    /// <param name="translationPath">Optional path to the translation file; when provided the session stage becomes <c>Translated</c>.</param>
    public void InjectTestTranscript(string transcriptPath, string? translationPath = null)
    {
        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                Stage = translationPath != null ? SessionWorkflowStage.Translated : SessionWorkflowStage.Transcribed,
                TranscriptPath = transcriptPath,
                TranslationPath = translationPath,
                StatusMessage = translationPath != null
                    ? "Test transcript and translation injected."
                    : "Test transcript injected.",
            };
        }
        SaveCurrentSession();
    }

    /// <summary>
    /// Resets the session pipeline to the MediaLoaded stage and clears all downstream artifacts, provider/model selections, language settings, speaker metadata, and generated timestamps.
    /// </summary>
    /// <remarks>
    /// Entry condition: safe to call at any time; the method is a no-op if the current session's stage is already earlier than MediaLoaded.
    /// Exit state on success: <c>CurrentSession.Stage</c> is set to <see cref="SessionWorkflowStage.MediaLoaded"/> and all transcription/translation/diarization/TTS-related fields are cleared or nulled, with <c>StatusMessage</c> set to "Ready.".
    /// Persistence: this method updates the in-memory <c>CurrentSession</c> only and does not persist the session to storage.
    /// Cancellation: not applicable.
    /// <summary>
    /// Reset the session pipeline to the MediaLoaded stage and clear all downstream artifacts and runtime/provider selections.
    /// </summary>
    /// <remarks>
    /// Expected entry state: any stage. If the current session stage is already earlier than MediaLoaded, the call is a no-op.
    /// On success: the session's Stage is set to MediaLoaded; transcript, translation, TTS, vocal separation artifacts, speaker/diarization metadata, and all runtime/provider/model selections are cleared; StatusMessage is set to "Ready.".
    /// Persistence: this method updates the in-memory CurrentSession only and does not persist the session to storage.
    /// Threading: updates are performed under an internal session lock to ensure thread safety.
    /// </remarks>
    public void ResetPipelineToMediaLoaded()
    {
        if (CurrentSession.Stage < SessionWorkflowStage.MediaLoaded) return;

        lock (_sessionLock)
        {
            CurrentSession = ResetToMediaLoadedSession(CurrentSession);
        }
    }

    public void ResetPipelineToTranscribed()
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Transcribed) return;
        
        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                Stage = SessionWorkflowStage.Transcribed,
                TranslationPath = null,
                TtsPath = null,
                MixedDubAudioPath = null,
                TtsVoice = null,
                TtsSegmentsPath = null,
                TtsSegmentAudioPaths = null,
                TtsSegmentDurations = null,
                TargetLanguage = null,
                TranslatedAtUtc = null,
                TtsGeneratedAtUtc = null,
                TranslationRuntime = null,
                TranslationProvider = null,
                TranslationModel = null,
                TtsRuntime = null,
                TtsProvider = null,
                SpeakerVoiceAssignments = null,
                SpeakerReferenceAudioPaths = null,
                DefaultTtsVoiceFallback = null,
                DiarizationProvider = null,
                SpeakersDetectedAtUtc = null,
                StatusMessage = "Reset to transcription."
            };
        }
    }

    public void ResetPipelineToDiarized()
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Diarized || !HasDiarizationMarker(CurrentSession))
            return;

        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                Stage = SessionWorkflowStage.Diarized,
                TranslationPath = null,
                TtsPath = null,
                MixedDubAudioPath = null,
                TtsVoice = null,
                TtsSegmentsPath = null,
                TtsSegmentAudioPaths = null,
                TtsSegmentDurations = null,
                TargetLanguage = null,
                TranslatedAtUtc = null,
                TtsGeneratedAtUtc = null,
                TranslationRuntime = null,
                TranslationProvider = null,
                TranslationModel = null,
                TtsRuntime = null,
                TtsProvider = null,
                StatusMessage = "Reset to speaker analysis."
            };
        }
    }

    public void ResetPipelineToTranslated()
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Translated) return;
        
        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                Stage = SessionWorkflowStage.Translated,
                TtsPath = null,
                MixedDubAudioPath = null,
                TtsVoice = null,
                TtsSegmentsPath = null,
                TtsSegmentAudioPaths = null,
                TtsSegmentDurations = null,
                TtsGeneratedAtUtc = null,
                TtsRuntime = null,
                TtsProvider = null,
                StatusMessage = "Reset to translation."
            };
        }
    }

    /// <summary>
    /// Clears translation and downstream artifacts so translation can be re-run, using the same rules as translation settings invalidation.
    /// </summary>
    public void ResetPipelineForTranslationRetry()
    {
        if (HasDiarizationMarker(CurrentSession))
            ResetPipelineToDiarized();
        else
            ResetPipelineToTranscribed();

        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with { StatusMessage = "Ready." };
        }
        SaveCurrentSession();
    }

    public void ClearPipeline()
    {
        ResetPipelineToMediaLoaded();
        InvalidateAllProviderCaches();
        SaveCurrentSession();
    }

    /// <summary>
    /// Apply a new pipeline settings selection (transcription, translation, TTS providers/models/profiles and optional target language) and update pipeline state, provider instances, and persistence as needed.
    /// </summary>
    /// <param name="selection">The chosen provider/model/profile/voice and optional target language to apply to the pipeline.</param>
    /// <returns>
    /// A <see cref="PipelineSettingsApplyResult"/> describing which pipeline stage (if any) was invalidated, the resulting session stage, whether settings were applied, and a status message.
    /// </returns>
    /// <summary>
    /// Applies a new pipeline settings selection, updates in-memory settings, resets downstream pipeline stages when required, and persists the session state when the pipeline is invalidated.
    /// </summary>
    /// <param name="selection">The pipeline settings to apply (transcription, translation, and TTS runtimes, providers, models, and voice; may include an optional target language and transcription language hint).</param>
    /// <returns>
    /// A <see cref="PipelineSettingsApplyResult"/> describing which pipeline category (if any) was invalidated, the session stage after applying the settings, whether changes were applied, and a human-readable status message.
    /// </returns>
    /// <remarks>
    /// Entry state: expects a loaded <c>CurrentSession</c> (foundation or any later stage). On success this method updates <c>CurrentSettings</c> and, when settings change causes pipeline invalidation, resets the affected downstream stage (to MediaLoaded, Transcribed/Diarized, or Translated as appropriate) and persists the session via <c>SaveCurrentSession()</c>. If no pipeline invalidation occurs the session is not persisted. This method does not observe cancellation tokens.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when any required field on <paramref name="selection"/> (transcription provider/model, translation provider/model, TTS provider/voice) is null, empty, or whitespace.</exception>
    public PipelineSettingsApplyResult ApplyPipelineSettings(PipelineSettingsSelection selection)
    {
        var stopwatch = Stopwatch.StartNew();
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.TranscriptionProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.TranscriptionModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.TranslationProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.TranslationModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.TtsProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.TtsVoice);

        var transcriptionProviderChanged =
            CurrentSettings.TranscriptionProfile != selection.TranscriptionRuntime ||
            !string.Equals(CurrentSettings.TranscriptionProvider, selection.TranscriptionProvider, StringComparison.Ordinal) ||
            !string.Equals(CurrentSettings.TranscriptionModel, selection.TranscriptionModel, StringComparison.Ordinal) ||
            !SessionSnapshotSemantics.TranscriptionLanguageHintsMatch(
                CurrentSettings.TranscriptionLanguageHint,
                selection.TranscriptionLanguageHint);
        var translationProviderChanged =
            CurrentSettings.TranslationProfile != selection.TranslationRuntime ||
            !string.Equals(CurrentSettings.TranslationProvider, selection.TranslationProvider, StringComparison.Ordinal) ||
            !string.Equals(CurrentSettings.TranslationModel, selection.TranslationModel, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(selection.TargetLanguage) &&
             !LanguageCode.TargetLanguagesMatch(CurrentSettings.TargetLanguage, selection.TargetLanguage));
        var ttsProviderChanged =
            CurrentSettings.TtsProfile != selection.TtsRuntime ||
            !string.Equals(CurrentSettings.TtsProvider, selection.TtsProvider, StringComparison.Ordinal) ||
            !string.Equals(CurrentSettings.TtsVoice, selection.TtsVoice, StringComparison.Ordinal);

        var settingsChanged = transcriptionProviderChanged || translationProviderChanged || ttsProviderChanged;
        if (!settingsChanged)
        {
            _log.Info(
                $"ApplyPipelineSettings: no-op at stage {CurrentSession.Stage}; selection matched current settings.");
            return new PipelineSettingsApplyResult(
                PipelineInvalidation.None,
                CurrentSession.Stage,
                false,
                CurrentSession.StatusMessage);
        }

        CurrentSettings.TranscriptionProfile = selection.TranscriptionRuntime;
        CurrentSettings.TranscriptionProvider = selection.TranscriptionProvider;
        CurrentSettings.TranscriptionModel = selection.TranscriptionModel;
        CurrentSettings.TranslationProfile = selection.TranslationRuntime;
        CurrentSettings.TranslationProvider = selection.TranslationProvider;
        CurrentSettings.TranslationModel = selection.TranslationModel;
        CurrentSettings.TtsProfile = selection.TtsRuntime;
        CurrentSettings.TtsProvider = selection.TtsProvider;
        CurrentSettings.TtsVoice = selection.TtsVoice;
        if (!string.IsNullOrWhiteSpace(selection.TargetLanguage))
        {
            CurrentSettings.TargetLanguage = LanguageCode.NormalizeForPersistence(selection.TargetLanguage)
                ?? selection.TargetLanguage.Trim();
        }

        CurrentSettings.TranscriptionLanguageHint =
            SessionSnapshotSemantics.NormalizeTranscriptionLanguageHint(selection.TranscriptionLanguageHint);

        if (transcriptionProviderChanged) _transcriptionService = null;
        if (translationProviderChanged) _translationService = null;
        if (ttsProviderChanged)
        {
            (_ttsService as IDisposable)?.Dispose();
            _ttsService = null;
        }
        (_vocalSeparationProvider as IDisposable)?.Dispose();
        _vocalSeparationProvider = null;

        var invalidation = CheckSettingsInvalidation();
        _log.Info(
            $"ApplyPipelineSettings: stage={CurrentSession.Stage}, invalidation={invalidation}, " +
            $"selection=({selection.TranscriptionRuntime}/{selection.TranscriptionProvider}/{selection.TranscriptionModel}, " +
            $"{selection.TranslationRuntime}/{selection.TranslationProvider}/{selection.TranslationModel}, " +
            $"{selection.TtsRuntime}/{selection.TtsProvider}/{selection.TtsVoice}, target={selection.TargetLanguage ?? "<unchanged>"}, asrHint={selection.TranscriptionLanguageHint ?? "<auto>"}), " +
            $"provenance=({SessionSnapshotSemantics.DescribeSessionProvenance(CurrentSession)})");
        var statusMessage = invalidation switch
        {
            PipelineInvalidation.Transcription => "Transcription settings changed — pipeline reset to media-loaded state.",
            PipelineInvalidation.Translation => HasDiarizationMarker(CurrentSession)
                ? "Translation settings changed — pipeline reset to speaker-mapped state."
                : "Translation settings changed — pipeline reset to transcript state.",
            PipelineInvalidation.Tts => "TTS settings changed — pipeline reset to translation state.",
            _ => "Pipeline settings updated."
        };

        switch (invalidation)
        {
            case PipelineInvalidation.Transcription:
                ResetPipelineToMediaLoaded();
                lock (_sessionLock)
                {
                    CurrentSession = CurrentSession with { StatusMessage = statusMessage };
                }
                SaveCurrentSession();
                break;
            case PipelineInvalidation.Translation:
                if (HasDiarizationMarker(CurrentSession))
                    ResetPipelineToDiarized();
                else
                    ResetPipelineToTranscribed();
                lock (_sessionLock)
                {
                    CurrentSession = CurrentSession with { StatusMessage = statusMessage };
                }
                SaveCurrentSession();
                break;
            case PipelineInvalidation.Tts:
                ResetPipelineToTranslated();
                lock (_sessionLock)
                {
                    CurrentSession = CurrentSession with { StatusMessage = statusMessage };
                }
                SaveCurrentSession();
                break;
        }

        RequestContainerizedAutostartForSettings();
        NotifySettingsModified();
        stopwatch.Stop();
        _log.Info(
            $"ApplyPipelineSettings complete: invalidation={invalidation}, stage={CurrentSession.Stage}, elapsedMs={stopwatch.ElapsedMilliseconds}");

        return new PipelineSettingsApplyResult(
            invalidation,
            CurrentSession.Stage,
            true,
            statusMessage);
    }

    /// <summary>
    /// Regenerates TTS audio for a single translated segment and updates the session's TTS segment paths.
    /// </summary>
    /// <param name="segmentId">The identifier of the translated segment to regenerate TTS for.</param>
    /// <remarks>
    /// Preconditions: the session must have a translation artifact available (TranslationPath set and readable).
    /// On success: updates CurrentSession.TtsSegmentAudioPaths with the regenerated segment audio path, sets a status message, and persists the session.
    /// Readiness guard: ensures the configured TTS runtime/provider is ready; if the provider is not ready and no model download is required a <see cref="PipelineProviderException"/> is thrown.
    /// Cancellation: this method does not accept or observe a cancellation token and cannot be cancelled.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when no translation is available or the specified segment text is missing.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the translation file referenced by the session cannot be found.</exception>
    /// <summary>
    /// Regenerates the TTS audio for a single translated segment and updates the session with the new segment audio path.
    /// </summary>
    /// <remarks>
    /// Entry requirements: the session must have a persisted translation file at <c>CurrentSession.TranslationPath</c> and the translation must contain the specified <paramref name="segmentId"/>. On success the method leaves the session at the same pipeline stage but updates <c>CurrentSession.TtsSegmentAudioPaths</c> for the regenerated segment and sets <c>StatusMessage</c>. The method persists the session before returning. 
    /// Guard conditions: the configured TTS runtime/provider must be ready for execution; if a containerized runtime is configured the container probe readiness is checked. If readiness indicates the provider is not ready and a model download is not required, a <see cref="PipelineProviderException"/> is thrown. 
    /// Cancellation: this method does not observe a cancellation token and will run to completion or failure.
    /// </remarks>
    /// <param name="segmentId">The identifier of the segment to regenerate (the id present in the translation segments).</param>
    /// <exception cref="InvalidOperationException">Thrown when the session has no translation, when the specified segment text is missing, or when TTS generation fails.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the translation file referenced by the session cannot be found on disk.</exception>
    /// <exception cref="PipelineProviderException">Thrown when the configured TTS provider is not ready for execution and no model download is required.</exception>
    public async Task RegenerateSegmentTtsAsync(string segmentId)
    {
        if (string.IsNullOrEmpty(CurrentSession.TranslationPath))
        {
            throw new InvalidOperationException("No translation available. Please translate first.");
        }

        if (!File.Exists(CurrentSession.TranslationPath))
        {
            throw new FileNotFoundException($"Translation file not found: {CurrentSession.TranslationPath}");
        }

        var segmentText = await _artifactReader.GetTranslatedTextAsync(CurrentSession.TranslationPath, segmentId);

        if (string.IsNullOrEmpty(segmentText))
        {
            throw new InvalidOperationException($"Segment not found: {segmentId}");
        }

        var translation = await _artifactReader.LoadTranslationAsync(CurrentSession.TranslationPath);
        var targetSegment = translation.Segments?.FirstOrDefault(s => s.Id == segmentId);
        var regenVoice = targetSegment is not null
            ? ResolveVoiceForSegment(targetSegment, CurrentSession.TtsVoice ?? CurrentSettings.TtsVoice)
            : CurrentSession.TtsVoice ?? CurrentSettings.TtsVoice;
        await EnsureSingleSpeakerQwenReferenceClipAsync();
        var referenceAudioPath = targetSegment is not null
            ? ResolveReferenceAudioForSegment(targetSegment)
            : null;

        await EnsureContainerizedExecutionRuntimeStartedAsync(CurrentSettings.TtsRuntime, "TTS");

        var readiness = CurrentSettings.TtsRuntime == InferenceRuntime.Containerized && _containerizedProbe is not null
            ? await ContainerizedProviderReadiness.CheckTtsForExecutionAsync(CurrentSettings, _containerizedProbe)
            : TtsRegistry.CheckReadiness(
                CurrentSettings.TtsProvider,
                regenVoice,
                CurrentSettings,
                KeyStore,
                CurrentSettings.TtsProfile);
        if (!readiness.IsReady && !readiness.RequiresModelDownload)
            throw new PipelineProviderException(readiness.BlockingReason!);

        _ttsService ??= CreateTtsService();

        var sessionDir = GetSessionDirectory();
        var mediaName = Path.GetFileNameWithoutExtension(CurrentSession.TranslationPath!);
        var segmentsDir = Path.Combine(sessionDir, "tts", "segments", mediaName);
        Directory.CreateDirectory(segmentsDir);

        var segmentAudioPath = Path.Combine(segmentsDir, $"{segmentId}.mp3");

        _log.Info($"Regenerating TTS for segment {segmentId}: {segmentText[..Math.Min(30, segmentText.Length)]}...");

        var targetLanguage = CurrentSession.TargetLanguage ?? CurrentSettings.TargetLanguage;
        var ttsTask = _inferenceEngine.GenerateSegmentTtsAsync(
            _ttsService,
            new SingleSegmentTtsRequest(
                segmentText,
                segmentAudioPath,
                regenVoice,
                targetSegment?.SpeakerId,
                referenceAudioPath,
                Language: targetLanguage));
        TrackPendingTtsTask(ttsTask);
        var result = await ttsTask;

        if (!result.Success)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown TTS error";
            _log.Error($"Segment TTS regeneration failed: {errorMsg}", new Exception(errorMsg));
            throw new InvalidOperationException($"Segment TTS regeneration failed: {errorMsg}");
        }

        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                TtsSegmentAudioPaths = CurrentSession.TtsSegmentAudioPaths is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [segmentId] = segmentAudioPath,
                    }
                    : new Dictionary<string, string>(CurrentSession.TtsSegmentAudioPaths, StringComparer.Ordinal)
                    {
                        [segmentId] = segmentAudioPath,
                    },
                StatusMessage = $"Regenerated TTS for segment {segmentId}.",
            };
        }

        _log.Info($"Segment TTS regenerated: {segmentId} -> {segmentAudioPath}");
        SaveCurrentSession();
    }

    /// <summary>
    /// Regenerates the translated text for a single segment in the current session's translation artifact.
    /// </summary>
    /// <param name="segmentId">Identifier of the segment to regenerate (stable segment id produced by SegmentId).</param>
    /// <remarks>
    /// Preconditions: the session must have a translation artifact path (CurrentSession.TranslationPath) and that file must exist; source and target languages must be set on the session. The method ensures the translation execution runtime is ready before invoking translation. On success the session's status message is updated and the session is persisted via SaveCurrentSession; the session pipeline stage is not advanced by this operation. This method does not support cancellation.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the session lacks a translation path, when the segment source text is missing, when source or target language is not set, or when the translation attempt fails.</exception>
    /// <summary>
    /// Regenerates the translated text for a single segment and persists the updated session state.
    /// </summary>
    /// <param name="segmentId">The identifier of the segment to regenerate (e.g., "segment_0.0").</param>
    /// <remarks>
    /// Entry requirements:
    /// - The current session must have a translation artifact available (TranslationPath set and file exists).
    /// - Source and target languages must be set on the current session (transcription and initial translation completed).
    /// 
    /// On success:
    /// - The session remains at the translated stage (no stage downgrade) and the session's StatusMessage is updated.
    /// - The coordinator persists the session via SaveCurrentSession().
    /// 
    /// Guard and readiness behavior:
    /// - Ensures the translation execution runtime is ready before invoking the translation engine; if readiness cannot be obtained the method will fail.
    /// - This method does not accept a cancellation token and does not support cooperative cancellation.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when translation is not available, source/target language is unset, or the segment's source text cannot be found.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the translation file referenced by CurrentSession.TranslationPath does not exist.</exception>
    public async Task RegenerateSegmentTranslationAsync(string segmentId)
    {
        if (string.IsNullOrEmpty(CurrentSession.TranslationPath))
        {
            throw new InvalidOperationException("No translation available. Please translate first.");
        }

        if (!File.Exists(CurrentSession.TranslationPath))
        {
            throw new FileNotFoundException($"Translation file not found: {CurrentSession.TranslationPath}");
        }

        var sourceText = await _artifactReader.GetSourceTextAsync(CurrentSession.TranslationPath, segmentId);

        if (string.IsNullOrEmpty(sourceText))
        {
            throw new InvalidOperationException($"Source text not found for segment: {segmentId}");
        }

        await EnsureTranslationExecutionReadyAsync();

        _translationService ??= CreateTranslationService();

        if (string.IsNullOrEmpty(CurrentSession.SourceLanguage))
            throw new InvalidOperationException("Source language is not set in the current session. Transcription must be completed first.");
            
        if (string.IsNullOrEmpty(CurrentSession.TargetLanguage))
            throw new InvalidOperationException("Target language is not set in the current session.");

        var sourceLanguage = CurrentSession.SourceLanguage;
        var targetLanguage = CurrentSession.TargetLanguage;

        _log.Info($"Regenerating translation for segment {segmentId}: {sourceText[..Math.Min(30, sourceText.Length)]}...");

        var result = await _inferenceEngine.TranslateSingleSegmentAsync(
            _translationService,
            new SingleSegmentTranslationRequest(
                sourceText,
                segmentId,
                CurrentSession.TranslationPath,
                CurrentSession.TranslationPath,
                sourceLanguage,
                targetLanguage,
                CurrentSession.TranslationModel ?? CurrentSettings.TranslationModel));

        if (!result.Success)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown translation error";
            _log.Error($"Segment translation regeneration failed: {errorMsg}", new Exception(errorMsg));
            throw new InvalidOperationException($"Segment translation regeneration failed: {errorMsg}");
        }

        _log.Info($"Segment translation regenerated: {segmentId}");
        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                StatusMessage = $"Regenerated translation for segment {segmentId}.",
            };
        }
        SaveCurrentSession();
    }

    public async Task<List<WorkflowSegmentState>> GetSegmentWorkflowListAsync()
    {
        return [.. await _artifactReader.BuildWorkflowSegmentsAsync(CurrentSession)];
    }

    // Stable segment ID derived from start time — must match the format written by translation providers.
    // Python: f"segment_{start}" → e.g. "segment_0.0", "segment_3.68"
    internal static string SegmentId(double start) =>
        start == (int)start
            ? FormattableString.Invariant($"segment_{start:0.0}")
            : FormattableString.Invariant($"segment_{start}");

    /// <summary>
    /// Base filename (no extension) for transcript JSON under <c>transcripts/</c>.
    /// Prefers the ingested media file name so vocal-separation stem paths (for example <c>vocals.wav</c>)
    /// do not replace the user-visible artifact name derived from the original loaded file.
    /// </summary>
    internal static string ResolveTranscriptArtifactStem(string? ingestedMediaPath, string transcriptionSourcePath)
    {
        if (!string.IsNullOrWhiteSpace(ingestedMediaPath))
            return Path.GetFileNameWithoutExtension(ingestedMediaPath);
        return Path.GetFileNameWithoutExtension(transcriptionSourcePath);
    }

    /// <summary>
    /// Builds TTS output file paths for a translation artifact and ensures their parent directories exist.
    /// Sanitizes the voice identifier so that reserved path characters don't produce invalid file names.
    /// </summary>
    /// <param name="translationPath">Full path to the translation artifact JSON file.</param>
    /// <param name="voice">Voice identifier used to name the combined MP3 output file.</param>
    /// <returns>
    /// A tuple of <c>TtsPath</c> (full path to the per-translation MP3) and <c>SegmentsDir</c>
    /// (directory for per-segment audio files); both directories are created if they do not exist.
    /// </returns>
    internal static (string TtsPath, string SegmentsDir) BuildTtsOutputPaths(string translationPath, string voice)
    {
        var sessionDir = Path.GetDirectoryName(Path.GetDirectoryName(translationPath)!)!;
        var ttsDir = Path.Combine(sessionDir, "tts");
        Directory.CreateDirectory(ttsDir);
        var fileName = Path.GetFileNameWithoutExtension(translationPath);
        // Sanitize the voice identifier so reserved/path characters don't produce invalid file names.
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitizedVoice = string.Concat((voice ?? string.Empty).Split(invalidChars)).Trim();
        if (sanitizedVoice.Length == 0) sanitizedVoice = "default";
        var ttsPath = Path.Combine(ttsDir, $"{fileName}_{sanitizedVoice}.mp3");
        var segmentsDir = Path.Combine(ttsDir, "segments", Path.GetFileNameWithoutExtension(translationPath));
        Directory.CreateDirectory(segmentsDir);
        return (ttsPath, segmentsDir);
    }

    private string GetSessionDirectory() => SessionDirectoryFor(CurrentSession.SessionId);

    private string SessionDirectoryFor(Guid sessionId) =>
        _sessionSwitchService.GetSessionDirectory(sessionId);

    /// <summary>
    /// Restores a previously-opened session by ID, stashing the current one first.
    /// The coordinator queues a declarative media reload request for the view layer.
    /// <summary>
    /// Restores a persisted session by its identifier and switches the coordinator to that restored session.
    /// </summary>
    /// <param name="sessionId">The GUID of the session to restore from in-memory cache or disk.</param>
    /// <remarks>
    /// Entry expectations: none beyond a valid <paramref name="sessionId"/>; if the coordinator currently has a loaded media session, that session will be stashed to the MRU/per-session caches before switching. 
    /// On success: validates and applies the restored snapshot to <c>CurrentSession</c>, updates <c>LastUpdatedAtUtc</c> and a stage-appropriate <c>StatusMessage</c>, queues a media reload request, persists the updated session to storage, and refreshes the recent-sessions list. 
    /// If artifact validation downgrades the snapshot's stage, a warning is logged and the downgraded snapshot is applied. 
    /// If the session ID is not found, the method logs a warning and returns without modifying <c>CurrentSession</c> or persisting state. 
    /// Cancellation: none (synchronous, non-cancellable). 
    /// </remarks>
    public void RestoreSession(Guid sessionId)
    {
        // Try in-memory cache first, then fall back to disk.
        var restored = _sessionSwitchService.LoadSession(sessionId, _mediaSnapshotCache);

        if (restored is null)
        {
            _log.Warning($"RestoreSession: session {sessionId} not found in cache or on disk.");
            return;
        }

        // Stash and persist the current session before switching.
        if (!string.IsNullOrEmpty(CurrentSession.SourceMediaPath))
        {
            RecentSessions = _sessionSwitchService.StashCurrentSession(
                CurrentSession,
                _mediaSnapshotCache,
                MediaSnapshotCacheLimit);
        }

        var validation = SessionSnapshotSemantics.ValidateArtifacts(restored);
        var validated = validation.Snapshot;
        if (validation.OriginalStage != validated.Stage)
        {
            _log.Warning(
                $"ValidateArtifacts[session-restore]: downgraded stage {validation.OriginalStage} -> {validated.Stage}; " +
                $"cleared={string.Join(",", validation.ClearedArtifacts)}; provenance={SessionSnapshotSemantics.DescribeSessionProvenance(validated)}");
        }
        var nowUtc = DateTimeOffset.UtcNow;
        lock (_sessionLock)
        {
            CurrentSession = validated with
            {
                LastUpdatedAtUtc = nowUtc,
                StatusMessage = validated.Stage >= SessionWorkflowStage.TtsGenerated
                    ? "Restored session with TTS. Ready for playback."
                    : validated.Stage >= SessionWorkflowStage.Translated
                        ? "Restored session with translation. Ready for TTS/dubbing."
                    : validated.Stage >= SessionWorkflowStage.Diarized
                        ? "Restored session with speaker mapping. Ready to resume translation/TTS."
                        : validated.Stage >= SessionWorkflowStage.Transcribed
                            ? "Restored session with transcript. Ready for translation."
                        : validated.Stage >= SessionWorkflowStage.MediaLoaded
                                ? "Restored session with media. Ready for transcription."
                                : "Restored foundation session.",
            };
        }

        _log.Info($"Restored session {sessionId} (stage: {CurrentSession.Stage}).");
        QueueMediaReloadRequest(autoPlay: false, "session-restore");
        SaveCurrentSession();
        RecentSessions = _recentStore.Load();
    }

    /// <summary>
    /// Computes which pipeline stages must be invalidated based on the current session's artifacts and the active pipeline settings.
    /// </summary>
    /// <summary>
    /// Determines which pipeline stages must be reset based on the current session snapshot and active settings.
    /// </summary>
    /// <remarks>
    /// Entry state: reads <see cref="CurrentSession"/> and <see cref="CurrentSettings"/> to compute invalidation; no mutation of coordinator state is performed. 
    /// Exit state: leaves session and settings unchanged and does not persist any data. 
    /// This method is synchronous and does not support cancellation. 
    /// </remarks>
    /// <returns>A <see cref="PipelineInvalidation"/> value that indicates which pipeline stages (if any) require reset.</returns>
    public PipelineInvalidation CheckSettingsInvalidation()
    {
        var cs = CurrentSession;
        var s  = CurrentSettings;

        var effectiveStage = SessionSnapshotSemantics.ResolveArtifactStage(cs);
        var invalidation = SessionSnapshotSemantics.ComputeInvalidation(cs, s);

        _log.Info(
            $"CheckSettingsInvalidation: stage={cs.Stage}, effectiveStage={effectiveStage}, invalidation={invalidation}, provenance=({SessionSnapshotSemantics.DescribeSessionProvenance(cs)})");
        return invalidation;
    }

    /// <summary>
    /// Records a pending TTS generation task for later observation and removes any completed tasks from the internal tracking list.
    /// </summary>
    /// <summary>
    /// Records a TTS-related task for in-flight tracking, pruning any already-completed tasks before adding the new task.
    /// </summary>
    /// <param name="task">The TTS-related <see cref="Task"/> to track; callers may await or monitor the snapshot returned by <see cref="SnapshotPendingTtsTasks"/>.</param>
    /// <remarks>
    /// Thread-safe: callers may invoke concurrently; the method acquires an internal lock while pruning completed tasks and adding the new task.
    /// </remarks>

    internal void TrackPendingTtsTask(Task task)
    {
        lock (_pendingTtsTasksLock)
        {
            _pendingTtsTasks.RemoveAll(static t => t.IsCompleted);
            _pendingTtsTasks.Add(task);
        }
    }

    /// <summary>
    /// Create a snapshot of currently tracked pending TTS generation tasks after pruning completed tasks.
    /// </summary>
    /// <returns>An array of pending TTS <see cref="Task"/> instances with completed tasks removed.</returns>
    /// <remarks>
    /// This method is thread-safe: it prunes completed tasks and captures the remaining tasks while holding the internal pending-task lock.
    /// It does not await, start, cancel, or otherwise modify the returned tasks beyond removing completed entries from the internal tracker.
    /// <summary>
    /// Return a thread-safe snapshot of the currently pending TTS generation tasks.
    /// </summary>
    /// <remarks>
    /// Prunes completed tasks from the internal pending list while holding a lock, then returns an array copy of the remaining tasks.
    /// This method does not await, start, cancel, or modify the returned tasks and does not persist session state.
    /// </remarks>
    /// <returns>An array of the remaining pending <see cref="Task"/> instances after pruning completed tasks.</returns>
    internal Task[] SnapshotPendingTtsTasks()
    {
        lock (_pendingTtsTasksLock)
        {
            _pendingTtsTasks.RemoveAll(static t => t.IsCompleted);
            return _pendingTtsTasks.ToArray();
        }
    }

    /// <summary>
    /// Processes an updated container probe result: records the probe state, updates runtime warmup text when the probe matches the current GPU service URL, updates bootstrap diagnostics and inference mode if any container-related diagnostics changed, and emits a readiness signal.
    /// </summary>
    /// <param name="probeResult">The latest probe result for a containerized inference service (URL, state, CUDA availability/version, and staleness).</param>
    /// <remarks>
    /// Side effects:
    /// - Updates the coordinator's probe-state cache for the probe's normalized base URL.
    /// - May update <see cref="RuntimeWarmupStatusText"/> when the probe URL matches the coordinator's effective GPU service URL and a non-empty warmup description is available.
    /// - Updates <see cref="BootstrapDiagnostics"/> container-related fields and recalculates <see cref="InferenceMode"/> when those diagnostics change.
    /// - Emits a readiness signal of kind <see cref="ReadinessSignalKind.ProbeResultUpdated"/>; <c>forceRefresh</c> is true when the probe URL is new, the probe state changed, or the probe state is not <see cref="ContainerizedProbeState.Checking"/>.
    /// <summary>
    /// Handles an updated container probe result by recording its state, updating bootstrap diagnostics and inference mode, optionally updating runtime warmup text, and emitting a readiness signal.
    /// </summary>
    /// <param name="probeResult">The probe result containing the service URL, probe state, CUDA availability and version, and staleness flag.</param>
    /// <remarks>
    /// Side effects:
    /// - Records the probe state in the coordinator's internal probe-state map.
    /// - Updates <see cref="BootstrapDiagnostics"/> with the probe's availability, URL, and CUDA info; when diagnostics change, updates <see cref="InferenceMode"/>.
    /// - If a containerized runtime is required and the probe's normalized URL matches <see cref="CurrentSettings.EffectiveGpuServiceUrl"/>, updates <see cref="RuntimeWarmupStatusText"/> when a non-empty warmup message is available.
    /// - Emits a <see cref="ReadinessSignalKind.ProbeResultUpdated"/> readiness signal; the signal's <c>forceRefresh</c> is true when the probe is new, its state changed, or its state is not <c>Checking</c>.
    /// </remarks>
    private void OnProbeResultUpdated(ContainerizedProbeResult probeResult)
    {
        var normalizedUrl = ContainerizedInferenceClient.NormalizeBaseUrl(probeResult.ServiceUrl);
        var hadPrevious = _lastProbeStates.TryGetValue(normalizedUrl, out var previousState);
        _lastProbeStates[normalizedUrl] = probeResult.State;
        var forceRefresh = !hadPrevious || previousState != probeResult.State || probeResult.State != ContainerizedProbeState.Checking;
        if (RequiresContainerizedRuntime()
            && string.Equals(
                normalizedUrl,
                ContainerizedInferenceClient.NormalizeBaseUrl(CurrentSettings.EffectiveGpuServiceUrl),
                StringComparison.OrdinalIgnoreCase))
        {
            var warmupText = DescribeRuntimeWarmupStatus(probeResult);
            if (!string.IsNullOrWhiteSpace(warmupText))
                RuntimeWarmupStatusText = warmupText;
        }

        var currentBootstrap = BootstrapDiagnostics;
        var updatedBootstrap = currentBootstrap with
        {
            ContainerizedServiceAvailable = probeResult.State == ContainerizedProbeState.Available,
            ContainerizedServiceUrl = string.IsNullOrWhiteSpace(probeResult.ServiceUrl)
                ? currentBootstrap.ContainerizedServiceUrl
                : probeResult.ServiceUrl,
            ContainerizedCudaAvailable = probeResult.CudaAvailable,
            ContainerizedCudaVersion = probeResult.CudaVersion ?? currentBootstrap.ContainerizedCudaVersion,
        };
        if (updatedBootstrap != currentBootstrap)
        {
            BootstrapDiagnostics = updatedBootstrap;
            InferenceMode = ResolveInferenceMode(updatedBootstrap);
        }

        EmitReadinessSignal(
            ReadinessSignalKind.ProbeResultUpdated,
            summary: $"Probe {probeResult.State}" + (probeResult.IsStale ? " (stale)" : string.Empty),
            source: normalizedUrl,
            forceRefresh: forceRefresh);
    }

    private void EmitReadinessSignal(
        ReadinessSignalKind kind,
        string summary,
        string? source = null,
        bool forceRefresh = false)
    {
        var signal = new ReadinessSignal(kind, DateTimeOffset.UtcNow, summary, source, forceRefresh);
        LastReadinessSignal = signal;
        ReadinessLastUpdatedUtc = signal.TimestampUtc;
        _readinessSignals.OnNext(signal);
    }

    partial void OnRuntimeWarmupStatusTextChanged(string? value) =>
        EmitReadinessSignal(
            ReadinessSignalKind.RuntimeWarmupStatusChanged,
            summary: string.IsNullOrWhiteSpace(value) ? "Runtime warmup idle." : value,
            source: nameof(RuntimeWarmupStatusText),
            forceRefresh: true);

    partial void OnBootstrapDiagnosticsChanged(BootstrapDiagnostics value) =>
        EmitReadinessSignal(
            ReadinessSignalKind.BootstrapApplied,
            summary: value.DiagnosticSummary,
            source: nameof(BootstrapDiagnostics),
            forceRefresh: true);

    public sealed record BootstrapWarmupData(
        BootstrapDiagnostics Diagnostics,
        IReadOnlyList<WorkflowSessionSnapshot> Snapshots,
        InferenceMode ResolvedInferenceMode);

    /// <summary>
        /// Determines whether the provided session snapshot contains diarization markers (speaker segmentation information).
        /// </summary>
        /// <param name="snapshot">The session snapshot to inspect.</param>
        /// <returns>`true` if the snapshot contains diarization markers, `false` otherwise.</returns>
        private static bool HasDiarizationMarker(WorkflowSessionSnapshot snapshot) =>
        SessionSnapshotSemantics.HasDiarizationMarker(snapshot);

}