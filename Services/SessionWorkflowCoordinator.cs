using System;
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
    public ITranscriptionRegistry TranscriptionRegistry { get; }
    public ITranslationRegistry TranslationRegistry { get; }
    public ITtsRegistry TtsRegistry { get; }
    public IDiarizationRegistry? DiarizationRegistry { get; private set; }
    private ITranscriptionProvider? _transcriptionService;
    private ITranslationProvider? _translationService;
    private ITtsProvider? _ttsService;

    private readonly IMediaTransportManager _transportManager;
    private bool _subscribedToSegmentEvents;
    private readonly EventHandler _segmentEndedHandler;
    private readonly EventHandler<Exception> _segmentErrorHandler;
    private readonly Dictionary<string, WorkflowSessionSnapshot> _mediaSnapshotCache =
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
    private BootstrapDiagnostics _bootstrapDiagnostics = new(false, null, false, null, false, null, false, false, null, null);

    [ObservableProperty]
    private HardwareSnapshot _hardwareSnapshot = HardwareSnapshot.Detecting;

    [ObservableProperty]
    private InferenceMode _inferenceMode = InferenceMode.SubprocessCpu;

    [ObservableProperty]
    private MediaReloadRequest? _pendingMediaReloadRequest;

    [ObservableProperty]
    private double _ttsPlaybackRate = 1.0;

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
        ContainerizedServiceProbe? containerizedProbe = null)
    {
        _store = store;
        _log = log;
        _perSessionStore = perSessionStore;
        _recentStore = recentStore;
        _artifactReader = artifactReader ?? new SessionArtifactReader();
        _sessionSwitchService = sessionSwitchService ?? new SessionSwitchService(perSessionStore, recentStore, log);
        _containerizedProbe = containerizedProbe;
        TranscriptionRegistry = transcriptionRegistry;
        TranslationRegistry = translationRegistry;
        TtsRegistry = ttsRegistry;
        DiarizationRegistry = diarizationRegistry;
        CurrentSettings = settings;
        KeyStore = keyStore;
        _transportManager = transportManager ?? new MediaTransportManager(
            segmentPlayer,
            sourcePlayer,
            new VideoPlaybackOptions(
                HwdecMode:      settings.VideoHwdec,
                GpuApi:         settings.VideoGpuApi,
                UseGpuNext:     settings.VideoUseGpuNext,
                VsrEnabled:     settings.VideoVsrEnabled,
                VsrQuality:     settings.VideoVsrQuality,
                HdrEnabled:     settings.VideoHdrEnabled,
                ToneMapping:    settings.VideoToneMapping,
                TargetPeak:     settings.VideoTargetPeak,
                HdrComputePeak: settings.VideoHdrComputePeak));

        // Create event handler delegates once for proper unsubscription
        _segmentEndedHandler = (_, _) => StopTtsPlayback();
        _segmentErrorHandler = (_, ex) => StopTtsPlayback();
    }

    public void UpdateSettings(AppSettings settings)
    {
        settings.NormalizeLegacyInferenceSettings();

        bool transcriptionProviderChanged = settings.TranscriptionRuntime != CurrentSettings.TranscriptionRuntime
            || settings.TranscriptionProvider != CurrentSettings.TranscriptionProvider
            || settings.TranscriptionModel != CurrentSettings.TranscriptionModel;
        bool translationProviderChanged = settings.TranslationRuntime != CurrentSettings.TranslationRuntime
            || settings.TranslationProvider != CurrentSettings.TranslationProvider
            || settings.TranslationModel != CurrentSettings.TranslationModel;
        bool ttsProviderChanged = settings.TtsRuntime != CurrentSettings.TtsRuntime
            || settings.TtsProvider != CurrentSettings.TtsProvider
            || settings.TtsVoice != CurrentSettings.TtsVoice
            || settings.PiperModelDir != CurrentSettings.PiperModelDir;

        CurrentSettings = settings;

        if (transcriptionProviderChanged) _transcriptionService = null;
        if (translationProviderChanged) _translationService = null;
        if (ttsProviderChanged) _ttsService = null;
    }

    public MediaReloadRequest? ConsumePendingMediaReloadRequest()
    {
        var request = PendingMediaReloadRequest;
        PendingMediaReloadRequest = null;
        return request;
    }

    private ITranscriptionProvider CreateTranscriptionService() =>
        TranscriptionRegistry.CreateProvider(
            CurrentSettings.TranscriptionProvider,
            CurrentSettings,
            KeyStore,
            CurrentSettings.TranscriptionRuntime);

    private ITranslationProvider CreateTranslationService() =>
        TranslationRegistry.CreateProvider(
            CurrentSettings.TranslationProvider,
            CurrentSettings,
            KeyStore,
            CurrentSettings.TranslationRuntime);

    private ITtsProvider CreateTtsService() =>
        TtsRegistry.CreateProvider(
            CurrentSettings.TtsProvider,
            CurrentSettings,
            KeyStore,
            CurrentSettings.TtsRuntime);

    /// <summary>
    /// Invalidates all cached provider service instances, forcing them to be recreated
    /// on the next pipeline execution with fresh CurrentSettings. Called explicitly
    /// when user clicks Clear or when a complete reset is needed.
    /// </summary>
    public void InvalidateAllProviderCaches()
    {
        _transcriptionService = null;
        _translationService = null;
        _ttsService = null;
    }

    /// <summary>
    /// Raises SettingsModified so subscribers (e.g. MainWindowViewModel) can persist changes.
    /// Call after any in-place mutation of CurrentSettings.
    /// </summary>
    public void NotifySettingsModified() => SettingsModified?.Invoke();

    // ── Diarization ───────────────────────────────────────────────────────────

    private async Task RunDiarizationAsync(string audioPath, string transcriptPath, CancellationToken ct)
    {
        if (DiarizationRegistry is null) return;

        var readiness = DiarizationRegistry.CheckReadiness(CurrentSettings.DiarizationProvider, CurrentSettings, KeyStore);
        if (!readiness.IsReady)
        {
            _log.Warning($"Diarization skipped: {readiness.BlockingReason}");
            return;
        }

        var provider = DiarizationRegistry.CreateProvider(CurrentSettings.DiarizationProvider, CurrentSettings, KeyStore);

        _log.Info($"Running diarization: provider={CurrentSettings.DiarizationProvider}, audio={audioPath}");
        var result = await provider.DiarizeAsync(new DiarizationRequest(audioPath), ct);

        if (!result.Success)
        {
            _log.Warning($"Diarization failed: {result.ErrorMessage}");
            return;
        }

        await MergeDiarizationIntoTranscriptAsync(transcriptPath, result.Segments, ct);

        CurrentSession = CurrentSession with
        {
            DiarizationProvider = CurrentSettings.DiarizationProvider,
            SpeakersDetectedAtUtc = DateTimeOffset.UtcNow,
        };
        SaveCurrentSession();

        _log.Info($"Diarization complete: {result.SpeakerCount} speakers across {result.Segments.Count} segments.");
    }

    private static async Task MergeDiarizationIntoTranscriptAsync(
        string transcriptPath,
        IReadOnlyList<DiarizedSegment> diarizedSegments,
        CancellationToken ct)
    {
        var artifact = await ArtifactJson.LoadTranscriptAsync(transcriptPath, ct);

        if (artifact.Segments is null) return;

        foreach (var segment in artifact.Segments)
            segment.SpeakerId = FindBestSpeakerFor(segment.Start, segment.End, diarizedSegments);

        var json = ArtifactJson.SerializeTranscript(artifact);
        await File.WriteAllTextAsync(transcriptPath, json, ct);
    }

    private static string FindBestSpeakerFor(double start, double end, IReadOnlyList<DiarizedSegment> diarizedSegments)
    {
        string? best = null;
        double bestOverlap = 0;
        foreach (var d in diarizedSegments)
        {
            var overlapStart = Math.Max(start, d.StartSeconds);
            var overlapEnd = Math.Min(end, d.EndSeconds);
            var overlap = Math.Max(0, overlapEnd - overlapStart);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = d.SpeakerId;
            }
        }
        return best ?? "spk_00";
    }

    public IReadOnlyDictionary<string, string> GetSpeakerVoiceAssignments() =>
        CurrentSession.SpeakerVoiceAssignments is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(CurrentSession.SpeakerVoiceAssignments, StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> GetSpeakerReferenceAudioPaths() =>
        CurrentSession.SpeakerReferenceAudioPaths is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(CurrentSession.SpeakerReferenceAudioPaths, StringComparer.Ordinal);

    public void SetSpeakerVoiceAssignment(string speakerId, string voiceOrModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceOrModel);

        var current = CurrentSession.SpeakerVoiceAssignments ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var updated = new Dictionary<string, string>(current, StringComparer.Ordinal)
        {
            [speakerId] = voiceOrModel,
        };

        CurrentSession = CurrentSession with { SpeakerVoiceAssignments = updated };
        SaveCurrentSession();
    }

    public void RemoveSpeakerVoiceAssignment(string speakerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        if (CurrentSession.SpeakerVoiceAssignments is null)
            return;

        var updated = new Dictionary<string, string>(CurrentSession.SpeakerVoiceAssignments, StringComparer.Ordinal);
        if (!updated.Remove(speakerId))
            return;

        CurrentSession = CurrentSession with { SpeakerVoiceAssignments = updated.Count == 0 ? null : updated };
        SaveCurrentSession();
    }

    public void SetSpeakerReferenceAudioPath(string speakerId, string clipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clipPath);

        var current = CurrentSession.SpeakerReferenceAudioPaths ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var updated = new Dictionary<string, string>(current, StringComparer.Ordinal)
        {
            [speakerId] = clipPath,
        };

        CurrentSession = CurrentSession with { SpeakerReferenceAudioPaths = updated };
        SaveCurrentSession();
    }

    public void RemoveSpeakerReferenceAudioPath(string speakerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        if (CurrentSession.SpeakerReferenceAudioPaths is null)
            return;

        var updated = new Dictionary<string, string>(CurrentSession.SpeakerReferenceAudioPaths, StringComparer.Ordinal);
        if (!updated.Remove(speakerId))
            return;

        CurrentSession = CurrentSession with { SpeakerReferenceAudioPaths = updated.Count == 0 ? null : updated };
        SaveCurrentSession();
    }

    public void SetMultiSpeakerEnabled(bool enabled)
    {
        if (CurrentSession.MultiSpeakerEnabled == enabled)
            return;

        CurrentSession = CurrentSession with { MultiSpeakerEnabled = enabled };
        SaveCurrentSession();
    }

    private string ResolveVoiceForSegment(TranslationSegmentArtifact segment, string defaultVoice)
    {
        if (!CurrentSession.MultiSpeakerEnabled)
            return defaultVoice;

        var speakerId = segment.SpeakerId;
        if (string.IsNullOrWhiteSpace(speakerId))
            return !string.IsNullOrWhiteSpace(CurrentSession.DefaultTtsVoiceFallback)
                ? CurrentSession.DefaultTtsVoiceFallback
                : defaultVoice;

        if (CurrentSession.SpeakerVoiceAssignments is not null &&
            CurrentSession.SpeakerVoiceAssignments.TryGetValue(speakerId, out var mappedVoice) &&
            !string.IsNullOrWhiteSpace(mappedVoice))
        {
            return mappedVoice;
        }

        return !string.IsNullOrWhiteSpace(CurrentSession.DefaultTtsVoiceFallback)
            ? CurrentSession.DefaultTtsVoiceFallback
            : defaultVoice;
    }

    private string? ResolveReferenceAudioForSegment(TranslationSegmentArtifact segment)
    {
        if (!CurrentSession.MultiSpeakerEnabled)
            return null;

        var speakerId = segment.SpeakerId;
        if (string.IsNullOrWhiteSpace(speakerId) || CurrentSession.SpeakerReferenceAudioPaths is null)
            return null;

        return CurrentSession.SpeakerReferenceAudioPaths.TryGetValue(speakerId, out var path) &&
               !string.IsNullOrWhiteSpace(path)
            ? path
            : null;
    }

    private void QueueMediaReloadRequest(bool autoPlay, string reason)
    {
        if (string.IsNullOrWhiteSpace(CurrentSession.IngestedMediaPath))
            return;

        PendingMediaReloadRequest = new MediaReloadRequest(
            CurrentSession.IngestedMediaPath,
            autoPlay,
            reason);
    }

    private IMediaTransport GetOrCreateSegmentPlayer()
    {
        var player = _transportManager.GetOrCreateSegmentPlayer();
        player.PlaybackRate = TtsPlaybackRate;

        // Subscribe to segment lifecycle events exactly once.
        if (!_subscribedToSegmentEvents)
        {
            player.Ended        += _segmentEndedHandler;
            player.ErrorOccurred += _segmentErrorHandler;
            _subscribedToSegmentEvents = true;
        }

        return player;
    }

    partial void OnTtsPlaybackRateChanged(double value)
    {
        if (_transportManager.SegmentPlayer is { } player)
            player.PlaybackRate = value;
    }

    public async Task PlayTtsForSegmentAsync(string segmentId)
    {
        if (CurrentSession is null)
            throw new InvalidOperationException("No active session.");

        var paths = CurrentSession.TtsSegmentAudioPaths;
        if (paths is null || !paths.TryGetValue(segmentId, out var audioPath))
            throw new InvalidOperationException($"No TTS audio path for segment '{segmentId}'.");

        if (!File.Exists(audioPath))
            throw new FileNotFoundException($"TTS audio file not found: {audioPath}", audioPath);

        StopTtsPlayback();
        PlaybackState = PlaybackState.PlayingSingleSegment;

        var player = GetOrCreateSegmentPlayer();
        player.Load(audioPath);
        ActiveTtsSegmentId = segmentId;
        await Task.Run(() => player.Play());
    }

    public void StopTtsPlayback()
    {
        try
        {
            _transportManager.SegmentPlayer?.Pause();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown/race path: segment transport was disposed while timer tick tried to stop playback.
        }
        ActiveTtsSegmentId = null;
        PlaybackState = PlaybackState.Idle;
    }

    public void StopPlayback()
    {
        StopTtsPlayback();
        StopSourceMedia();
    }

    public async Task PlaySourceMediaAtSegmentAsync(string segmentId)
    {
        if (CurrentSession is null)
            throw new InvalidOperationException("No active session.");

        if (string.IsNullOrEmpty(CurrentSession.IngestedMediaPath))
            throw new InvalidOperationException("No media loaded.");

        if (!File.Exists(CurrentSession.IngestedMediaPath))
            throw new FileNotFoundException($"Ingested media not found: {CurrentSession.IngestedMediaPath}");

        var segments = await GetSegmentWorkflowListAsync();
        var target = segments.Find(s => s.SegmentId == segmentId);
        if (target is null)
            throw new InvalidOperationException($"Segment not found: {segmentId}");

        var player = GetOrCreateSourcePlayer();
        player.Load(CurrentSession.IngestedMediaPath);
        player.Seek((long)(target.StartSeconds * 1000));
        await Task.Run(() => player.Play());
        _log.Info($"Playing source media at segment {segmentId} ({target.StartSeconds:F1}s)");
    }

    public void StopSourceMedia()
    {
        _transportManager.SourceMediaPlayer?.Pause();
    }

    public IMediaTransport GetOrCreateSourcePlayer() =>
        _transportManager.GetOrCreateSourcePlayer();

    public IMediaTransport? SourceMediaPlayer => _transportManager.SourceMediaPlayer;

    public void Dispose()
    {
        FlushPendingSave();

        // Unsubscribe segment events before disposing the transport manager.
        if (_subscribedToSegmentEvents)
        {
            var segmentPlayer = _transportManager.GetOrCreateSegmentPlayer();
            segmentPlayer.Ended         -= _segmentEndedHandler;
            segmentPlayer.ErrorOccurred -= _segmentErrorHandler;
            _subscribedToSegmentEvents = false;
        }

        _transportManager.Dispose();
    }

    public string StateFilePath => _store.StateFilePath;

    public string LogFilePath => _log.LogFilePath;
    internal AppLog Log => _log;
    internal ContainerizedServiceProbe? ContainerizedProbe => _containerizedProbe;

    public void Initialize()
    {
        // Heavy bootstrap probes and per-session snapshot preloading are warmed in background.
        BootstrapDiagnostics = new BootstrapDiagnostics(false, null, false, null, false, null, false, false, null, null);

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
        var diagnostics = BootstrapDiagnostics.Run(CurrentSettings.EffectiveContainerizedServiceUrl);
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
            return InferenceMode.Containerized;
        // ManagedVenv path is PLACEHOLDER — not yet implemented.
        return InferenceMode.SubprocessCpu;
    }

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

        QueueMediaReloadRequest(autoPlay: true, "media-switch");
        SaveCurrentSession();
    }

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
            StatusMessage = "Pipeline reset. Ready to run."
        };
        SaveCurrentSession();
    }

    public async Task TranscribeMediaAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(CurrentSession.IngestedMediaPath))
            throw new InvalidOperationException("No media loaded. Please load media first.");

        if (!File.Exists(CurrentSession.IngestedMediaPath))
            throw new FileNotFoundException($"Ingested media file not found: {CurrentSession.IngestedMediaPath}");

        // Registry-owned readiness check — covers IsImplemented, API keys, local model status
        var readiness = CurrentSettings.TranscriptionRuntime == InferenceRuntime.Containerized && _containerizedProbe is not null
            ? await ContainerizedProviderReadiness.CheckTranscriptionForExecutionAsync(
                CurrentSettings,
                _containerizedProbe,
                cancellationToken)
            : TranscriptionRegistry.CheckReadiness(
                CurrentSettings.TranscriptionProvider,
                CurrentSettings.TranscriptionModel,
                CurrentSettings,
                KeyStore,
                CurrentSettings.TranscriptionRuntime);
        if (!readiness.IsReady && !readiness.RequiresModelDownload)
            throw new PipelineProviderException(readiness.BlockingReason!);

        // Registry-owned model download — provider-agnostic
        if (readiness.RequiresModelDownload)
        {
            if (!await TranscriptionRegistry.EnsureModelAsync(
                    CurrentSettings.TranscriptionProvider,
                    CurrentSettings.TranscriptionModel,
                    CurrentSettings,
                    progress,
                    cancellationToken,
                    CurrentSettings.TranscriptionRuntime))
                throw new InvalidOperationException($"Failed to download model '{CurrentSettings.TranscriptionModel}'.");
        }

        _transcriptionService ??= CreateTranscriptionService();

        var sessionDir = GetSessionDirectory();
        var transcriptDir = Path.Combine(sessionDir, "transcripts");
        Directory.CreateDirectory(transcriptDir);

        var fileName = Path.GetFileNameWithoutExtension(CurrentSession.IngestedMediaPath);
        var transcriptPath = Path.Combine(transcriptDir, $"{fileName}.json");

        var cpuThreads = CurrentSettings.TranscriptionCpuThreads > 0
            ? CurrentSettings.TranscriptionCpuThreads.ToString()
            : "auto";
        var cpuWorkers = Math.Max(1, CurrentSettings.TranscriptionNumWorkers);
        var routeSummary =
            $"provider={CurrentSettings.TranscriptionProvider}, model={CurrentSettings.TranscriptionModel}, " +
            $"cpu_compute={CurrentSettings.TranscriptionCpuComputeType}, cpu_threads={cpuThreads}, cpu_workers={cpuWorkers}";
        var hwSummary =
            $"avx2={(HardwareSnapshot.HasAvx2 ? "yes" : "no")}, " +
            $"avx512={(HardwareSnapshot.HasAvx512 ? "yes" : "no")}, " +
            $"cuda={(HardwareSnapshot.HasCuda ? "yes" : "no")}";

        _log.Info($"Starting transcription: {CurrentSession.IngestedMediaPath} " +
                  $"[{CurrentSettings.TranscriptionProvider}/{CurrentSettings.TranscriptionModel}] " +
                  $"route=({routeSummary}) hw=({hwSummary})");

        var result = await _transcriptionService.TranscribeAsync(
            new TranscriptionRequest(
                CurrentSession.IngestedMediaPath,
                transcriptPath,
                CurrentSettings.TranscriptionModel,
                null,
                CurrentSettings.TranscriptionCpuComputeType,
                CurrentSettings.TranscriptionCpuThreads,
                CurrentSettings.TranscriptionNumWorkers),
            cancellationToken);

        if (!result.Success)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown transcription error";
            _log.Error($"Transcription failed: {errorMsg}", new Exception(errorMsg));
            throw new InvalidOperationException($"Transcription failed: {errorMsg}");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.Transcribed,
            TranscriptPath = transcriptPath,
            SourceLanguage = result.Language,
            TranscribedAtUtc = nowUtc,
            TranscriptionRuntime = CurrentSettings.TranscriptionRuntime,
            TranscriptionProvider = CurrentSettings.TranscriptionProvider,
            TranscriptionModel = CurrentSettings.TranscriptionModel,
            StatusMessage = $"Transcribed {result.Segments.Count} segments ({result.Language}). Ready for translation.",
        };

        _log.Info($"Transcription complete: {result.Segments.Count} segments, language: {result.Language}");
        SaveCurrentSession();

        if (CurrentSession.MultiSpeakerEnabled && !string.IsNullOrEmpty(CurrentSettings.DiarizationProvider))
            await RunDiarizationAsync(CurrentSession.IngestedMediaPath!, transcriptPath, cancellationToken);
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
            StatusMessage = "Pipeline reset to transcribed state."
        };
        SaveCurrentSession();
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
        SaveCurrentSession();
    }

    public void ClearPipeline()
    {
        ResetPipelineToMediaLoaded();
        InvalidateAllProviderCaches();
    }

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
            CurrentSettings.TranscriptionRuntime != selection.TranscriptionRuntime ||
            !string.Equals(CurrentSettings.TranscriptionProvider, selection.TranscriptionProvider, StringComparison.Ordinal) ||
            !string.Equals(CurrentSettings.TranscriptionModel, selection.TranscriptionModel, StringComparison.Ordinal);
        var translationProviderChanged =
            CurrentSettings.TranslationRuntime != selection.TranslationRuntime ||
            !string.Equals(CurrentSettings.TranslationProvider, selection.TranslationProvider, StringComparison.Ordinal) ||
            !string.Equals(CurrentSettings.TranslationModel, selection.TranslationModel, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(selection.TargetLanguage) &&
             !string.Equals(CurrentSettings.TargetLanguage, selection.TargetLanguage, StringComparison.Ordinal));
        var ttsProviderChanged =
            CurrentSettings.TtsRuntime != selection.TtsRuntime ||
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

        CurrentSettings.TranscriptionRuntime = selection.TranscriptionRuntime;
        CurrentSettings.TranscriptionProvider = selection.TranscriptionProvider;
        CurrentSettings.TranscriptionModel = selection.TranscriptionModel;
        CurrentSettings.TranslationRuntime = selection.TranslationRuntime;
        CurrentSettings.TranslationProvider = selection.TranslationProvider;
        CurrentSettings.TranslationModel = selection.TranslationModel;
        CurrentSettings.TtsRuntime = selection.TtsRuntime;
        CurrentSettings.TtsProvider = selection.TtsProvider;
        CurrentSettings.TtsVoice = selection.TtsVoice;
        if (!string.IsNullOrWhiteSpace(selection.TargetLanguage))
            CurrentSettings.TargetLanguage = selection.TargetLanguage;

        if (transcriptionProviderChanged) _transcriptionService = null;
        if (translationProviderChanged) _translationService = null;
        if (ttsProviderChanged) _ttsService = null;

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
            PipelineInvalidation.Translation => "Translation settings changed — pipeline reset to transcript state.",
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

    public async Task TranslateTranscriptAsync(IProgress<double>? progress = null, string? targetLanguage = null, string? sourceLanguage = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(CurrentSession.TranscriptPath))
            throw new InvalidOperationException("No transcript available. Please transcribe media first.");

        if (!File.Exists(CurrentSession.TranscriptPath))
            throw new FileNotFoundException($"Transcript file not found: {CurrentSession.TranscriptPath}");

        var lang = targetLanguage ?? CurrentSettings.TargetLanguage;
        var src = sourceLanguage ?? CurrentSession.SourceLanguage ?? "auto";

        // Registry-owned readiness check
        var readiness = CurrentSettings.TranslationRuntime == InferenceRuntime.Containerized && _containerizedProbe is not null
            ? await ContainerizedProviderReadiness.CheckTranslationForExecutionAsync(
                CurrentSettings,
                _containerizedProbe,
                cancellationToken)
            : TranslationRegistry.CheckReadiness(
                CurrentSettings.TranslationProvider,
                CurrentSettings.TranslationModel,
                CurrentSettings,
                KeyStore,
                CurrentSettings.TranslationRuntime);
        if (!readiness.IsReady && !readiness.RequiresModelDownload)
            throw new PipelineProviderException(readiness.BlockingReason!);

        if (readiness.RequiresModelDownload)
        {
            if (!await TranslationRegistry.EnsureModelAsync(
                    CurrentSettings.TranslationProvider,
                    CurrentSettings.TranslationModel,
                    CurrentSettings,
                    progress,
                    cancellationToken,
                    CurrentSettings.TranslationRuntime))
                throw new InvalidOperationException($"Failed to download model '{CurrentSettings.TranslationModel}'.");
        }

        _translationService ??= CreateTranslationService();

        var sessionDir = GetSessionDirectory();
        var translationDir = Path.Combine(sessionDir, "translations");
        Directory.CreateDirectory(translationDir);

        var fileName = Path.GetFileNameWithoutExtension(CurrentSession.TranscriptPath);
        var translationPath = Path.Combine(translationDir, $"{fileName}_{lang}.json");

        _log.Info($"Starting translation: {CurrentSession.TranscriptPath} ({src} -> {lang})");

        var result = await _translationService.TranslateAsync(
            new TranslationRequest(
                CurrentSession.TranscriptPath,
                translationPath,
                src,
                lang,
                CurrentSettings.TranslationModel),
            cancellationToken);

        if (!result.Success)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown translation error";
            _log.Error($"Translation failed: {errorMsg}", new Exception(errorMsg));
            throw new InvalidOperationException($"Translation failed: {errorMsg}");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.Translated,
            TranslationPath = translationPath,
            SourceLanguage = src,
            TargetLanguage = lang,
            TranslatedAtUtc = nowUtc,
            TranslationRuntime = CurrentSettings.TranslationRuntime,
            TranslationProvider = CurrentSettings.TranslationProvider,
            TranslationModel = CurrentSettings.TranslationModel,
            StatusMessage = $"Translated {result.Segments.Count} segments to {lang}. Ready for TTS/dubbing.",
        };

        _log.Info($"Translation complete: {result.Segments.Count} segments, {src} -> {lang}");
        SaveCurrentSession();
    }

    public async Task GenerateTtsAsync(IProgress<double>? progress = null, string? voice = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(CurrentSession.TranslationPath))
            throw new InvalidOperationException("No translation available. Please translate first.");

        if (!File.Exists(CurrentSession.TranslationPath))
            throw new FileNotFoundException($"Translation file not found: {CurrentSession.TranslationPath}");

        var v = voice ?? CurrentSettings.TtsVoice;

        // Registry-owned readiness check
        var readiness = CurrentSettings.TtsRuntime == InferenceRuntime.Containerized && _containerizedProbe is not null
            ? await ContainerizedProviderReadiness.CheckTtsForExecutionAsync(
                CurrentSettings,
                _containerizedProbe,
                cancellationToken)
            : TtsRegistry.CheckReadiness(
                CurrentSettings.TtsProvider,
                v,
                CurrentSettings,
                KeyStore,
                CurrentSettings.TtsRuntime);
        if (!readiness.IsReady && !readiness.RequiresModelDownload)
            throw new PipelineProviderException(readiness.BlockingReason!);

        if (readiness.RequiresModelDownload)
        {
            if (!await TtsRegistry.EnsureModelAsync(
                    CurrentSettings.TtsProvider,
                    v,
                    CurrentSettings,
                    progress,
                    cancellationToken,
                    CurrentSettings.TtsRuntime))
                throw new InvalidOperationException($"Failed to download voice '{v}'.");
        }

        _ttsService ??= CreateTtsService();

        var sessionDir = GetSessionDirectory();
        var ttsDir = Path.Combine(sessionDir, "tts");
        Directory.CreateDirectory(ttsDir);

        var fileName = Path.GetFileNameWithoutExtension(CurrentSession.TranslationPath);
        var ttsPath = Path.Combine(ttsDir, $"{fileName}_{v}.mp3");

        _log.Info($"Starting TTS generation: {CurrentSession.TranslationPath} -> {ttsPath}");

        var result = await _ttsService.GenerateTtsAsync(
            new TtsRequest(
                CurrentSession.TranslationPath,
                ttsPath,
                v,
                CurrentSession.SpeakerVoiceAssignments,
                CurrentSession.SpeakerReferenceAudioPaths,
                CurrentSession.DefaultTtsVoiceFallback),
            cancellationToken);

        if (!result.Success)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown TTS error";
            _log.Error($"TTS failed: {errorMsg}", new Exception(errorMsg));
            throw new InvalidOperationException($"TTS failed: {errorMsg}");
        }

        _log.Info($"TTS complete: {ttsPath}, size: {result.FileSizeBytes} bytes");

        var mediaName = Path.GetFileNameWithoutExtension(CurrentSession.TranslationPath!);
        var segmentsDir = Path.Combine(ttsDir, "segments", mediaName);
        Directory.CreateDirectory(segmentsDir);

        // Generate per-segment TTS audio for dubbed playback
        var segmentAudioPaths = new Dictionary<string, string>();
        int totalSegments = 0;
        try
        {
            var translationData = await _artifactReader.LoadTranslationAsync(CurrentSession.TranslationPath, cancellationToken);

            foreach (var seg in translationData.Segments ?? [])
            {
                var id = seg.Id;
                var text = seg.TranslatedText;

                if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(id))
                {
                    _log.Info($"Skipping segment {id}: empty text or ID");
                    continue;
                }

                totalSegments++;
                var segmentAudioPath = Path.Combine(segmentsDir, $"{id}.mp3");
                var resolvedVoice = ResolveVoiceForSegment(seg, v);
                var referenceAudioPath = ResolveReferenceAudioForSegment(seg);

                _log.Info($"Generating TTS for segment {id} (voice={resolvedVoice}, speaker={seg.SpeakerId ?? "<none>"}): {text.Substring(0, Math.Min(30, text.Length))}...");

                try
                {
                    var segResult = await _ttsService.GenerateSegmentTtsAsync(
                        new SingleSegmentTtsRequest(
                            text,
                            segmentAudioPath,
                            resolvedVoice,
                            seg.SpeakerId,
                            referenceAudioPath),
                        cancellationToken);

                    if (segResult.Success && File.Exists(segmentAudioPath))
                    {
                        segmentAudioPaths[id] = segmentAudioPath;
                        _log.Info($"Segment TTS generated: {id} -> {segmentAudioPath}");
                    }
                    else
                    {
                        _log.Warning($"Segment TTS failed or file missing: {id}");
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"Segment TTS generation failed for {id}: {ex.Message}", ex);
                    // Continue with next segment instead of crashing
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Error generating per-segment TTS: {ex.Message}", ex);
        }

        int succeeded = segmentAudioPaths.Count;
        if (totalSegments > 0 && succeeded == 0)
        {
            _log.Error("TTS stage completed but no segments were generated.", new InvalidOperationException("Zero TTS segments"));
            throw new InvalidOperationException(
                "TTS stage completed but no segments were generated. Check provider configuration and logs.");
        }

        string ttsStatusMessage = (succeeded == totalSegments)
            ? $"TTS generated ({v}). Dubbing complete."
            : $"TTS generated ({v}). {succeeded}/{totalSegments} segments ready — {totalSegments - succeeded} failed.";

        var nowUtc = DateTimeOffset.UtcNow;
        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TtsPath = ttsPath,
            TtsVoice = v,
            TtsGeneratedAtUtc = nowUtc,
            TtsSegmentsPath = segmentsDir,
            TtsSegmentAudioPaths = segmentAudioPaths,
            TtsRuntime = CurrentSettings.TtsRuntime,
            TtsProvider = CurrentSettings.TtsProvider,
            StatusMessage = ttsStatusMessage,
        };

        SaveCurrentSession();
    }

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
        var referenceAudioPath = targetSegment is not null
            ? ResolveReferenceAudioForSegment(targetSegment)
            : null;

        var readiness = CurrentSettings.TtsRuntime == InferenceRuntime.Containerized && _containerizedProbe is not null
            ? await ContainerizedProviderReadiness.CheckTtsForExecutionAsync(CurrentSettings, _containerizedProbe)
            : TtsRegistry.CheckReadiness(
                CurrentSettings.TtsProvider,
                regenVoice,
                CurrentSettings,
                KeyStore,
                CurrentSettings.TtsRuntime);
        if (!readiness.IsReady && !readiness.RequiresModelDownload)
            throw new PipelineProviderException(readiness.BlockingReason!);

        _ttsService ??= CreateTtsService();

        var sessionDir = GetSessionDirectory();
        var mediaName = Path.GetFileNameWithoutExtension(CurrentSession.TranslationPath!);
        var segmentsDir = Path.Combine(sessionDir, "tts", "segments", mediaName);
        Directory.CreateDirectory(segmentsDir);

        var segmentAudioPath = Path.Combine(segmentsDir, $"{segmentId}.mp3");

        _log.Info($"Regenerating TTS for segment {segmentId}: {segmentText.Substring(0, Math.Min(30, segmentText.Length))}...");

        var result = await _ttsService.GenerateSegmentTtsAsync(
            new SingleSegmentTtsRequest(
                segmentText,
                segmentAudioPath,
                regenVoice,
                targetSegment?.SpeakerId,
                referenceAudioPath));

        if (!result.Success)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown TTS error";
            _log.Error($"Segment TTS regeneration failed: {errorMsg}", new Exception(errorMsg));
            throw new InvalidOperationException($"Segment TTS regeneration failed: {errorMsg}");
        }

        var currentSegments = CurrentSession.TtsSegmentAudioPaths ?? new Dictionary<string, string>();
        var updatedSegments = new Dictionary<string, string>(currentSegments)
        {
            [segmentId] = segmentAudioPath
        };

        CurrentSession = CurrentSession with
        {
            TtsSegmentAudioPaths = updatedSegments,
            StatusMessage = $"Regenerated TTS for segment {segmentId}.",
        };

        _log.Info($"Segment TTS regenerated: {segmentId} -> {segmentAudioPath}");
        SaveCurrentSession();
    }

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

        var readiness = CurrentSettings.TranslationRuntime == InferenceRuntime.Containerized && _containerizedProbe is not null
            ? await ContainerizedProviderReadiness.CheckTranslationForExecutionAsync(CurrentSettings, _containerizedProbe)
            : TranslationRegistry.CheckReadiness(
                CurrentSettings.TranslationProvider,
                CurrentSettings.TranslationModel,
                CurrentSettings,
                KeyStore,
                CurrentSettings.TranslationRuntime);
        if (!readiness.IsReady && !readiness.RequiresModelDownload)
            throw new PipelineProviderException(readiness.BlockingReason!);

        _translationService ??= CreateTranslationService();

        var sourceLanguage = CurrentSession.SourceLanguage ?? "es";
        var targetLanguage = CurrentSession.TargetLanguage ?? "en";

        _log.Info($"Regenerating translation for segment {segmentId}: {sourceText.Substring(0, Math.Min(30, sourceText.Length))}...");

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
    /// </summary>
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
    /// Advances the pipeline from its current stage through any remaining stages
    /// (Transcribe → Translate → GenerateTts) that have not yet completed.
    /// Stage-gating decisions live here, not in callers.
    /// </summary>
    public async Task AdvancePipelineAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stage = CurrentSession.Stage;

        if (stage < SessionWorkflowStage.Transcribed)
        {
            await TranscribeMediaAsync(progress, cancellationToken);
        }

        stage = CurrentSession.Stage;
        if (stage < SessionWorkflowStage.Translated)
        {
            await TranslateTranscriptAsync(progress, null, null, cancellationToken);
        }

        stage = CurrentSession.Stage;
        if (stage < SessionWorkflowStage.TtsGenerated)
        {
            await GenerateTtsAsync(progress, null, cancellationToken);
        }
    }

    public void SaveCurrentSession()
    {
        var snapshot = CurrentSession with { LastUpdatedAtUtc = DateTimeOffset.UtcNow };
        CurrentSession = snapshot;
        PersistSnapshot(snapshot, updateStatus: true);
    }

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

}
