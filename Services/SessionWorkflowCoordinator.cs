using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

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


    private readonly IMediaTransportManager _transportManager;
    private bool _subscribedToSegmentEvents;
    private bool _subscribedToSourceDiagnostics;
    private readonly EventHandler _segmentEndedHandler;
    private readonly EventHandler<Exception> _segmentErrorHandler;
    private readonly Action<VsrDiagnosticSnapshot> _vsrDiagnosticChangedHandler;
    private VsrDiagnosticSnapshot? _latestVsrDiagnostic;
    private readonly ConcurrentDictionary<string, WorkflowSessionSnapshot> _mediaSnapshotCache =
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

    public ApiKeyStore? KeyStore { get; private set; }

    public SessionWorkflowCoordinator(
        SessionSnapshotStore store,
        AppLog log,
        AppSettings settings,
        PerSessionSnapshotStore perSessionStore,
        RecentSessionsStore recentStore,
        ITranscriptionRegistry transcriptionRegistry,
        ITranslationRegistry translationRegistry,
        ITtsRegistry ttsRegistry,
        IMediaTransport? segmentPlayer,
        IMediaTransport? sourcePlayer,
        ApiKeyStore? keyStore = null)
        : this(
            store,
            log,
            settings,
            perSessionStore,
            recentStore,
            transcriptionRegistry,
            translationRegistry,
            ttsRegistry,
            transportManager: null,
            segmentPlayer: segmentPlayer,
            sourcePlayer: sourcePlayer,
            keyStore: keyStore)
    {
    }

    public SessionWorkflowCoordinator(
        SessionSnapshotStore store,
        AppLog log,
        AppSettings settings,
        PerSessionSnapshotStore perSessionStore,
        RecentSessionsStore recentStore,
        ITranscriptionRegistry transcriptionRegistry,
        ITranslationRegistry translationRegistry,
        ITtsRegistry ttsRegistry,
        IMediaTransportManager? transportManager = null,
        IMediaTransport? segmentPlayer = null,
        IMediaTransport? sourcePlayer = null,
        ApiKeyStore? keyStore = null,
        SessionArtifactReader? artifactReader = null,
        SessionSwitchService? sessionSwitchService = null,
        IDiarizationRegistry? diarizationRegistry = null,
        ContainerizedServiceProbe? containerizedProbe = null,
        IContainerizedInferenceManager? containerizedInferenceManager = null,
        IAudioProcessingService? audioProcessingService = null)

    {
        _store = store;
        _log = log;
        _perSessionStore = perSessionStore;
        _recentStore = recentStore;
        _containerizedProbe = containerizedProbe;
        _containerizedInferenceManager = containerizedInferenceManager;
        _audioProcessingService = audioProcessingService;
        _artifactReader = artifactReader ?? new SessionArtifactReader();
        _sessionSwitchService = sessionSwitchService ?? new SessionSwitchService(perSessionStore, recentStore, log);

        _cpuRuntimeManager = new ManagedCpuRuntimeManager(log);
        TranscriptionRegistry = transcriptionRegistry;
        TranslationRegistry = translationRegistry;
        TtsRegistry = ttsRegistry;
        DiarizationRegistry = diarizationRegistry;
        CurrentSettings = settings;
        KeyStore = keyStore;
        _transportManager = transportManager ?? new MediaTransportManager(
            segmentPlayer,
            sourcePlayer,
            videoOptionsFactory: () => new VideoPlaybackOptions(
                HwdecMode:      settings.VideoHwdec,
                GpuApi:         settings.VideoGpuApi,
                UseGpuNext:     settings.VideoUseGpuNext,
                VsrEnabled:     settings.VideoVsrEnabled,
                HdrEnabled:     settings.VideoHdrEnabled,
                AllowHdrPassthrough: settings.VideoHdrEnabled && HardwareSnapshot.QueryActiveHdrDisplay(),
                ToneMapping:    settings.VideoToneMapping,
                TargetPeak:     settings.VideoTargetPeak,
                HdrComputePeak: settings.VideoHdrComputePeak),
            log: log);

        // Create event handler delegates once for proper unsubscription
        _segmentEndedHandler = (_, _) => StopTtsPlayback();
        _segmentErrorHandler = (_, ex) => StopTtsPlayback();
        _vsrDiagnosticChangedHandler = RecordVsrDiagnosticSnapshot;

        RefreshVideoEnhancementDiagnostics();
    }

    public string StateFilePath => _store.StateFilePath;

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
                        ? "Resumed session after speaker mapping. Assign voices, then continue."
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
                            ? "Resumed session awaiting speaker mapping."
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
                MediaLoadedAtUtc = nowUtc,
                LastUpdatedAtUtc = nowUtc,
                StatusMessage = validated.Stage >= SessionWorkflowStage.TtsGenerated
                    ? "Restored prior TTS. Ready for playback."
                    : validated.Stage >= SessionWorkflowStage.Translated
                        ? "Restored translation. Ready for TTS/dubbing."
                        : validated.Stage >= SessionWorkflowStage.Diarized
                            ? "Restored speaker mapping state. Assign voices, then continue."
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
                MediaLoadedAtUtc = nowUtc,
                TranscriptPath = null,
                SourceLanguage = null,
                TranscribedAtUtc = null,
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

    private void ResetPipelineToDiarized()
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
            StatusMessage = "Pipeline reset to speaker mapping state."
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
            !string.Equals(CurrentSettings.TranscriptionModel, selection.TranscriptionModel, StringComparison.Ordinal);
        var translationProviderChanged =
            CurrentSettings.TranslationProfile != selection.TranslationRuntime ||
            !string.Equals(CurrentSettings.TranslationProvider, selection.TranslationProvider, StringComparison.Ordinal) ||
            !string.Equals(CurrentSettings.TranslationModel, selection.TranslationModel, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(selection.TargetLanguage) &&
             !string.Equals(CurrentSettings.TargetLanguage, selection.TargetLanguage, StringComparison.Ordinal));
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
            CurrentSettings.TargetLanguage = selection.TargetLanguage;

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
            $"{selection.TtsRuntime}/{selection.TtsProvider}/{selection.TtsVoice}, target={selection.TargetLanguage ?? "<unchanged>"}), " +
            $"provenance=({SessionSnapshotSemantics.DescribeSessionProvenance(CurrentSession)})");
        var statusMessage = invalidation switch
        {
            PipelineInvalidation.Transcription => "Transcription settings changed — pipeline reset to media-loaded state.",
            PipelineInvalidation.Translation => HasDiarizationMarker(CurrentSession)
                ? "Translation settings changed — pipeline reset to speaker mapping state."
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
        var ttsTask = _ttsService.GenerateSegmentTtsAsync(
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
    /// <exception cref="FileNotFoundException">Thrown when the current session's translation file cannot be found on disk.</exception>
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

        var result = await _translationService.TranslateSingleSegmentAsync(
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

    // Stable segment ID derived from start time — must match the format written by GoogleTranslationProvider.
    // Python: f"segment_{start}" → e.g. "segment_0.0", "segment_3.68"
    internal static string SegmentId(double start) =>
        start == (int)start
            ? FormattableString.Invariant($"segment_{start:0.0}")
            : FormattableString.Invariant($"segment_{start}");

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
                        ? "Restored session after speaker mapping. Assign voices, then continue."
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
        !string.IsNullOrWhiteSpace(snapshot.DiarizationProvider)
        && snapshot.SpeakersDetectedAtUtc.HasValue;

}
