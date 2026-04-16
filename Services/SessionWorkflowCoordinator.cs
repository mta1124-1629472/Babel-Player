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
using Babel.Player.Services.Credentials;
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
    private readonly ConcurrentBag<Task> _pendingTtsTasks = [];
    private readonly IAudioProcessingService? _audioProcessingService;


    private readonly IInferenceExecutionEngine _inferenceEngine;
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

    /// <summary>
    /// Raised when AppSettings are modified in-place (e.g. by left-panel dropdowns).
    /// Subscribers should call SettingsService.Save() in response.
    /// </summary>
    public event Action? SettingsModified;
    public IObservable<ReadinessSignal> ReadinessSignals => _readinessSignals;

    public ApiKeyStore? KeyStore { get; private set; }

    /// <summary>
    /// Creates a <see cref="SessionWorkflowCoordinator"/> with an explicit transport manager.
    /// Use this overload in production via <see cref="DependencyLocator"/>.
    /// <summary>
    /// Initializes a new <see cref="SessionWorkflowCoordinator"/> using the provided core services, media transport manager, registries, and optional components.
    /// </summary>
    /// <param name="coreServices">Core application services and shared dependencies (settings, store, logging) required by the coordinator.</param>
    /// <param name="transportManager">Media transport manager responsible for playback and segment transports for this coordinator.</param>
    /// <param name="registries">Registry bundle providing per-session stores and provider registries (transcription, translation, TTS, recent sessions).</param>
    /// <summary>
    /// Initializes a new SessionWorkflowCoordinator with the provided core services, transport manager, and registries, and prepares internal orchestration, runtime, and probe wiring required to manage the session workflow.
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
        _transcriptionOrchestrator = new TranscriptionOrchestrator(this);
        _translationOrchestrator = new TranslationOrchestrator(this);
        _diarizationStageOrchestrator = new DiarizationStageOrchestrator(this);
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
    /// Loads persisted coordinator state (if any), initializes the current session and bootstrap diagnostics, and prepares any required media reload and persistence state.
    /// </summary>
    /// <remarks>
    /// If a saved snapshot is present, artifacts are validated and the session may be downgraded; the validated snapshot becomes the active CurrentSession (with LastUpdatedAtUtc updated and an appropriate StatusMessage). If no snapshot is found, a new foundation session is created. The method also sets SessionSource and PersistenceStatus, loads RecentSessions, caches the session's media snapshot when applicable, queues a media reload request when the session has media, and persists the current session.
    /// </remarks>
    public void Initialize()
    {
        // Heavy bootstrap probes and per-session snapshot preloading are warmed in background.
        BootstrapDiagnostics = new BootstrapDiagnostics(false, null, false, null, false, null, false, false, null, null, "Detecting...");

        var nowUtc = DateTimeOffset.UtcNow;
        var loadResult = _store.Load();

        if (loadResult.Snapshot is null)
        {
            CurrentSession = WorkflowSessionSnapshot.CreateNew(nowUtc);
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

            string statusMessage = validated.Stage >= SessionWorkflowStage.TtsGenerated
                    ? "Resumed session with TTS. Dubbing complete."
                : validated.Stage >= SessionWorkflowStage.Translated
                    ? "Resumed session with translation. Ready for TTS/dubbing."
                : validated.Stage >= SessionWorkflowStage.Diarized
                    ? "Resumed session with speaker mapping. Ready to resume translation/TTS."
                    : validated.Stage >= SessionWorkflowStage.Transcribed
                        ? "Resumed session with transcript. Ready for translation."
                        : validated.Stage >= SessionWorkflowStage.MediaLoaded
                            ? "Resumed session with media. Ready for transcription."
                            : "Resumed saved foundation session. Workflow not yet started.";


            CurrentSession = validated with
            {
                LastUpdatedAtUtc = nowUtc,
                StatusMessage = statusMessage,
            };

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
    /// Loads the specified media file into the workflow, restoring a previously cached session for that media when available or creating a new session otherwise.
    /// </summary>
    /// <param name="sourceMediaPath">Absolute or relative path to the source media file to load.</param>
    /// <exception cref="FileNotFoundException">Thrown when <paramref name="sourceMediaPath"/> does not exist.</exception>
    /// <remarks>
    /// As a result of this call the coordinator copies the media into the session's artifact directory, updates <c>CurrentSession</c> (session id, stage, artifact paths, timestamps, and status message), queues a media reload request, and persists the session snapshot.
    /// </remarks>
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

            CurrentSession = validated with
            {
                IngestedMediaPath = ingestedPath,
                VocalsAudioPath = validated.VocalsAudioPath,
                InstrumentalAudioPath = validated.InstrumentalAudioPath,
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

            CurrentSession = CurrentSession with
            {
                SessionId = newSessionId,
                Stage = SessionWorkflowStage.MediaLoaded,
                SourceMediaPath = sourceMediaPath,
                IngestedMediaPath = ingestedPath,
                VocalsAudioPath = null,
                InstrumentalAudioPath = null,
                MediaLoadedAtUtc = nowUtc,
                TranscriptPath = null,
                SourceLanguage = null,
                TranscribedAtUtc = null,
                TranscriptionLanguageHint = null,
                TranslationPath = null,
                TargetLanguage = null,
                TranslatedAtUtc = null,
                TtsPath = null,
                TtsVoice = null,
                TtsGeneratedAtUtc = null,
                TtsSegmentsPath = null,
                TtsSegmentAudioPaths = null,
                StatusMessage = "Media loaded. Ready for transcription.",
            };
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

    public void InjectTestTranscript(string transcriptPath, string? translationPath = null)
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
        SaveCurrentSession();
    }

    public void ResetPipelineToMediaLoaded()
    {
        if (CurrentSession.Stage < SessionWorkflowStage.MediaLoaded) return;

        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.MediaLoaded,
            VocalsAudioPath = null,
            InstrumentalAudioPath = null,
            TranscriptPath = null,
            TranslationPath = null,
            TtsPath = null,
            TtsVoice = null,
            TtsSegmentsPath = null,
            TtsSegmentAudioPaths = null,
            SourceLanguage = null,
            TargetLanguage = null,
            TranscribedAtUtc = null,
            TranslatedAtUtc = null,
            TtsGeneratedAtUtc = null,
            TranscriptionRuntime = null,
            TranscriptionProvider = null,
            TranscriptionModel = null,
            TranscriptionLanguageHint = null,
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
            StatusMessage = "Pipeline reset. Ready to run."
        };
    }

    public void ResetPipelineToTranscribed()
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Transcribed) return;
        
        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.Transcribed,
            TranslationPath = null,
            TtsPath = null,
            TtsVoice = null,
            TtsSegmentsPath = null,
            TtsSegmentAudioPaths = null,
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
            StatusMessage = "Pipeline reset to transcribed state."
        };
    }

    public void ResetPipelineToDiarized()
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Diarized || !HasDiarizationMarker(CurrentSession))
            return;

        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.Diarized,
            TranslationPath = null,
            TtsPath = null,
            TtsVoice = null,
            TtsSegmentsPath = null,
            TtsSegmentAudioPaths = null,
            TargetLanguage = null,
            TranslatedAtUtc = null,
            TtsGeneratedAtUtc = null,
            TranslationRuntime = null,
            TranslationProvider = null,
            TranslationModel = null,
            TtsRuntime = null,
            TtsProvider = null,
            StatusMessage = "Pipeline reset to speaker-mapped state."
        };
    }

    public void ResetPipelineToTranslated()
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Translated) return;
        
        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.Translated,
            TtsPath = null,
            TtsVoice = null,
            TtsSegmentsPath = null,
            TtsSegmentAudioPaths = null,
            TtsGeneratedAtUtc = null,
            TtsRuntime = null,
            TtsProvider = null,
            StatusMessage = "Pipeline reset to translated state."
        };
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

        CurrentSession = CurrentSession with { StatusMessage = "Ready to re-run translation." };
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
                CurrentSession = CurrentSession with { StatusMessage = statusMessage };
                SaveCurrentSession();
                break;
            case PipelineInvalidation.Translation:
                if (HasDiarizationMarker(CurrentSession))
                    ResetPipelineToDiarized();
                else
                    ResetPipelineToTranscribed();
                CurrentSession = CurrentSession with { StatusMessage = statusMessage };
                SaveCurrentSession();
                break;
            case PipelineInvalidation.Tts:
                ResetPipelineToTranslated();
                CurrentSession = CurrentSession with { StatusMessage = statusMessage };
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
    /// Regenerates the TTS audio for a single translated segment and updates the current session with the generated audio path.
    /// </summary>
    /// <param name="segmentId">The identifier of the segment to regenerate (for example, "segment_0.0").</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no translation is available, the specified segment cannot be found in the translation, or TTS generation fails.
    /// </exception>
    /// <exception cref="FileNotFoundException">Thrown when the session's translation file is missing on disk.</exception>
    /// <summary>
    /// Regenerates the TTS audio file for a single translated segment and updates the session's TTS segment audio mapping.
    /// </summary>
    /// <param name="segmentId">Identifier of the translated segment to regenerate TTS for.</param>
    /// <remarks>
    /// Preconditions: <see cref="CurrentSession.TranslationPath"/> must be set and the translation file must exist; otherwise this method throws (<see cref="InvalidOperationException"/> or <see cref="FileNotFoundException"/>). The method ensures any required containerized runtime is started and checks provider readiness before generation; if the configured TTS provider is not ready for execution and a model download is not required, a <see cref="PipelineProviderException"/> is thrown. On success the session's <c>TtsSegmentAudioPaths</c> and <c>StatusMessage</c> are updated and the session is persisted via <see cref="SaveCurrentSession"/>. This method does not accept a cancellation token and does not support cooperative cancellation.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when no translation is available, the segment is not found, or TTS generation fails.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the translation file referenced by the session does not exist.</exception>
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
        _pendingTtsTasks.Add(ttsTask);
        var result = await ttsTask;

        if (!result.Success)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown TTS error";
            _log.Error($"Segment TTS regeneration failed: {errorMsg}", new Exception(errorMsg));
            throw new InvalidOperationException($"Segment TTS regeneration failed: {errorMsg}");
        }

        var currentSegments = CurrentSession.TtsSegmentAudioPaths ?? [];
        currentSegments[segmentId] = segmentAudioPath;

        CurrentSession = CurrentSession with
        {
            TtsSegmentAudioPaths = currentSegments,
            StatusMessage = $"Regenerated TTS for segment {segmentId}.",
        };

        _log.Info($"Segment TTS regenerated: {segmentId} -> {segmentAudioPath}");
        SaveCurrentSession();
    }

    /// <summary>
    /// Regenerates the translation for a single segment identified by its segment ID and updates the current session snapshot.
    /// </summary>
    /// <param name="segmentId">The identifier of the segment to retranslate (e.g., "segment_0.0").</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when there is no translation available for the current session, when the source text for the specified segment is missing,
    /// when the session's source or target language is not set, or when the translation operation fails.
    /// </exception>
    /// <summary>
    /// Regenerates the translated text for a single segment and updates session status.
    /// </summary>
    /// <param name="segmentId">The identifier of the segment to regenerate (stable segment id produced by SegmentId).</param>
    /// <remarks>
    /// Entry state: requires a current session with <see cref="WorkflowSessionSnapshot.TranslationPath"/> set and a translation file present on disk. On success: updates the session <see cref="WorkflowSessionSnapshot.StatusMessage"/> to indicate the regenerated segment and persists the session snapshot. This method observes the coordinator's translation execution readiness and will attempt to prepare the translation runtime before invoking translation. The operation honors cooperative cancellation if the coordinator's runtime readiness checks or the underlying translation pipeline support it; callers should use external cancellation by stopping coordinator-triggered workflows where applicable.
    /// Guard conditions: throws if the translation path is missing or the translation file is not found, if source or target language is not set, or if the segment source text cannot be located. If readiness checks indicate the translation cannot run (for example due to missing provider readiness), the method will throw an InvalidOperationException describing the failure.
    /// </remarks>
    /// <exception cref="FileNotFoundException">Thrown when the current session's translation file cannot be found on disk.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the session lacks a translation path, when the source or target language is not set, when the segment source text is not found, or when the translation attempt fails.</exception>
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
        CurrentSession = CurrentSession with
        {
            StatusMessage = $"Regenerated translation for segment {segmentId}.",
        };
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
    /// </summary>
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

        _log.Info($"Restored session {sessionId} (stage: {CurrentSession.Stage}).");
        QueueMediaReloadRequest(autoPlay: false, "session-restore");
        SaveCurrentSession();
        RecentSessions = _recentStore.Load();
    }

    /// <summary>
    /// Compares the current session's recorded provider/model settings against the
    /// active <see cref="CurrentSettings"/> to determine what has been invalidated.
    /// Callers use the result to decide which pipeline reset to apply before running.
    /// <summary>
    /// Computes which pipeline stages must be invalidated based on the current session's artifacts and the active pipeline settings.
    /// </summary>
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
    /// Updates the snapshot's LastUpdatedAtUtc, sets it as the current session, and persists that snapshot.
    /// </summary>
    /// <remarks>
    /// Sets <c>CurrentSession</c> to a copy with an updated <c>LastUpdatedAtUtc</c> and saves that snapshot to the configured persistence stores.
    /// </remarks>
    public void SaveCurrentSession()
    {
        var snapshot = CurrentSession with { LastUpdatedAtUtc = DateTimeOffset.UtcNow };
        CurrentSession = snapshot;
        PersistSnapshot(snapshot, updateStatus: true);
    }

    /// <summary>
    /// Immediately persists the current session snapshot to persistent stores after updating LastUpdatedAtUtc.
    /// </summary>
    /// <remarks>
    /// Updates the in-memory <c>CurrentSession</c> with the current UTC <c>LastUpdatedAtUtc</c> timestamp and then synchronously saves that snapshot to the underlying stores.
    /// <summary>
    /// Immediately persists the current session snapshot and updates its last-updated timestamp.
    /// </summary>
    /// <remarks>
    /// Updates CurrentSession.LastUpdatedAtUtc to the current UTC time, assigns the updated snapshot to CurrentSession, and synchronously saves the snapshot to all configured stores so the persistence status is updated immediately.
    /// </remarks>
    public void FlushPendingSave()
    {
        var snapshot = CurrentSession with { LastUpdatedAtUtc = DateTimeOffset.UtcNow };
        CurrentSession = snapshot;
        PersistSnapshot(snapshot, updateStatus: true);
    }

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

    private void PersistSnapshot(WorkflowSessionSnapshot snapshot, bool updateStatus)
    {
        var stopwatch = Stopwatch.StartNew();
        _store.Save(snapshot);
        _perSessionStore.Save(snapshot);
        stopwatch.Stop();
        var message = $"Saved current session snapshot to {StateFilePath}.";
        if (updateStatus)
            PersistenceStatus = message;
        _log.Info($"{message} Mirrored per-session snapshot. elapsedMs={stopwatch.ElapsedMilliseconds}");
    }

    public sealed record BootstrapWarmupData(
        BootstrapDiagnostics Diagnostics,
        IReadOnlyList<WorkflowSessionSnapshot> Snapshots,
        InferenceMode ResolvedInferenceMode);

    private static bool HasDiarizationMarker(WorkflowSessionSnapshot snapshot) =>
        SessionSnapshotSemantics.HasDiarizationMarker(snapshot);

}
