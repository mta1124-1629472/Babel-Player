using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator : ObservableObject, IDisposable
{
    private readonly SessionSnapshotStore _store;
    private readonly AppLog _log;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;
    private TranscriptionService? _transcriptionService;
    private TranslationService? _translationService;
    private TtsService? _ttsService;

    private readonly IMediaTransport? _injectedSegmentPlayer;
    private IMediaTransport? _segmentPlayer;
    private bool _subscribedToPlayerEvents;
    private readonly EventHandler? _segmentEndedHandler;
    private readonly EventHandler<Exception>? _segmentErrorHandler;

    private IMediaTransport? _sourceMediaPlayer;
    private readonly IMediaTransport? _injectedSourcePlayer;
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
    private BootstrapDiagnostics _bootstrapDiagnostics = new(false, null, false, null);

    public bool HasRecentSessions => RecentSessions.Count > 0;

    public AppSettings CurrentSettings { get; private set; }

    public SessionWorkflowCoordinator(
        SessionSnapshotStore store,
        AppLog log,
        AppSettings settings,
        PerSessionSnapshotStore perSessionStore,
        RecentSessionsStore recentStore,
        IMediaTransport? segmentPlayer = null,
        IMediaTransport? sourcePlayer = null)
    {
        _store = store;
        _log = log;
        _perSessionStore = perSessionStore;
        _recentStore = recentStore;
        CurrentSettings = settings;
        _injectedSegmentPlayer = segmentPlayer;
        _injectedSourcePlayer = sourcePlayer;

        // Create event handler delegates once for proper unsubscription
        _segmentEndedHandler = (_, _) => StopTtsPlayback();
        _segmentErrorHandler = (_, _) => StopTtsPlayback();
    }

    public void UpdateSettings(AppSettings settings)
    {
        CurrentSettings = settings;
    }

    private IMediaTransport GetOrCreateSegmentPlayer()
    {
        if (_segmentPlayer is not null) return _segmentPlayer;
        _segmentPlayer = _injectedSegmentPlayer ?? new LibMpvHeadlessTransport(suppressAudio: false);
        
        // Subscribe to events only once and track subscription state
        if (!_subscribedToPlayerEvents)
        {
            _segmentPlayer.Ended += _segmentEndedHandler;
            _segmentPlayer.ErrorOccurred += _segmentErrorHandler;
            _subscribedToPlayerEvents = true;
        }
        
        return _segmentPlayer;
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
        _segmentPlayer?.Pause();
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
        _sourceMediaPlayer?.Pause();
    }

    public IMediaTransport GetOrCreateSourcePlayer()
    {
        if (_sourceMediaPlayer is not null) return _sourceMediaPlayer;
        _sourceMediaPlayer = _injectedSourcePlayer ?? new LibMpvEmbeddedTransport();
        return _sourceMediaPlayer;
    }

    public IMediaTransport? SourceMediaPlayer => _sourceMediaPlayer;

    public void Dispose()
    {
        // Unsubscribe from events only if we previously subscribed
        if (_subscribedToPlayerEvents && _segmentPlayer is not null)
        {
            _segmentPlayer.Ended -= _segmentEndedHandler;
            _segmentPlayer.ErrorOccurred -= _segmentErrorHandler;
            _subscribedToPlayerEvents = false;
        }

        // Only dispose if we created the player; injected players are owned by the caller.
        if (_injectedSegmentPlayer is null)
        {
            _segmentPlayer?.Dispose();
        }

        if (_injectedSourcePlayer is null)
        {
            _sourceMediaPlayer?.Dispose();
        }
    }

    public string StateFilePath => _store.StateFilePath;

    public string LogFilePath => _log.LogFilePath;

    public void Initialize()
    {
        // Run dependency probes upfront so the UI can warn before the pipeline is attempted.
        BootstrapDiagnostics = BootstrapDiagnostics.Run();
        if (!BootstrapDiagnostics.AllDependenciesAvailable)
            _log.Warning($"Bootstrap: {BootstrapDiagnostics.DiagnosticSummary}");
        else
            _log.Info("Bootstrap: all dependencies available.");

        // Seed in-memory cache from per-session snapshot files so cross-restart media switching works.
        foreach (var snapshot in _perSessionStore.LoadAll())
        {
            if (!string.IsNullOrEmpty(snapshot.SourceMediaPath))
                _mediaSnapshotCache[MediaKey(snapshot.SourceMediaPath)] = snapshot;
        }

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
            var validated = ValidateArtifacts(snapshot);

            // Log any artifacts that were dropped by validation
            if (snapshot.Stage != validated.Stage)
                _log.Warning($"Session stage downgraded on load: {snapshot.Stage} → {validated.Stage} (missing artifacts)");

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
                _mediaSnapshotCache[MediaKey(CurrentSession.SourceMediaPath)] = CurrentSession;

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
        SaveCurrentSession();
    }

    public void LoadMedia(string sourceMediaPath)
    {
        if (!File.Exists(sourceMediaPath))
            throw new FileNotFoundException($"Source media file not found: {sourceMediaPath}");

        var nowUtc = DateTimeOffset.UtcNow;

        // Stash current snapshot before switching — persist to disk so it survives restart.
        if (!string.IsNullOrEmpty(CurrentSession.SourceMediaPath))
        {
            _mediaSnapshotCache[MediaKey(CurrentSession.SourceMediaPath)] = CurrentSession;
            _perSessionStore.Save(CurrentSession);
            _recentStore.Upsert(new RecentSessionEntry(
                CurrentSession.SessionId,
                CurrentSession.SourceMediaPath,
                Path.GetFileName(CurrentSession.SourceMediaPath),
                CurrentSession.Stage,
                CurrentSession.LastUpdatedAtUtc));
            RecentSessions = _recentStore.Load();
        }

        var newKey = MediaKey(sourceMediaPath);
        var switchingMedia = !string.IsNullOrEmpty(CurrentSession.SourceMediaPath)
            && !string.Equals(MediaKey(CurrentSession.SourceMediaPath), newKey,
                              StringComparison.OrdinalIgnoreCase);

        if (switchingMedia && _mediaSnapshotCache.TryGetValue(newKey, out var cached))
        {
            // Returning to a previously processed media — restore, validate, then copy into
            // that session's existing directory.
            var validated = ValidateArtifacts(cached);

            var sessionDir = SessionDirectoryFor(validated.SessionId);
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

            var sessionDir = SessionDirectoryFor(newSessionId);
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

        SaveCurrentSession();
    }

    private static string MediaKey(string path) => Path.GetFullPath(path);

    private static WorkflowSessionSnapshot ValidateArtifacts(WorkflowSessionSnapshot s)
    {
        var stage = s.Stage;

        if (stage >= SessionWorkflowStage.TtsGenerated
            && (string.IsNullOrEmpty(s.TtsPath) || !File.Exists(s.TtsPath)))
        {
            stage = SessionWorkflowStage.Translated;
            s = s with { TtsPath = null, TtsVoice = null, TtsGeneratedAtUtc = null,
                          TtsSegmentsPath = null, TtsSegmentAudioPaths = null };
        }

        if (stage >= SessionWorkflowStage.Translated
            && (string.IsNullOrEmpty(s.TranslationPath) || !File.Exists(s.TranslationPath)))
        {
            stage = SessionWorkflowStage.Transcribed;
            s = s with { TranslationPath = null, TargetLanguage = null, TranslatedAtUtc = null };
        }

        if (stage >= SessionWorkflowStage.Transcribed
            && (string.IsNullOrEmpty(s.TranscriptPath) || !File.Exists(s.TranscriptPath)))
        {
            stage = SessionWorkflowStage.MediaLoaded;
            s = s with { TranscriptPath = null, SourceLanguage = null, TranscribedAtUtc = null };
        }

        if (stage >= SessionWorkflowStage.MediaLoaded
            && (string.IsNullOrEmpty(s.IngestedMediaPath) || !File.Exists(s.IngestedMediaPath)))
        {
            stage = SessionWorkflowStage.Foundation;
            s = s with { IngestedMediaPath = null, MediaLoadedAtUtc = null };
        }

        return s with { Stage = stage };
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

    public async Task TranscribeMediaAsync()
    {
        if (string.IsNullOrEmpty(CurrentSession.IngestedMediaPath))
        {
            throw new InvalidOperationException("No media loaded. Please load media first.");
        }

        if (!File.Exists(CurrentSession.IngestedMediaPath))
        {
            throw new FileNotFoundException($"Ingested media file not found: {CurrentSession.IngestedMediaPath}");
        }

        _transcriptionService ??= new TranscriptionService(_log);

        var sessionDir = GetSessionDirectory();
        var transcriptDir = Path.Combine(sessionDir, "transcripts");
        Directory.CreateDirectory(transcriptDir);

        var fileName = Path.GetFileNameWithoutExtension(CurrentSession.IngestedMediaPath);
        var transcriptPath = Path.Combine(transcriptDir, $"{fileName}.json");

        _log.Info($"Starting transcription: {CurrentSession.IngestedMediaPath}");

        var result = await _transcriptionService.TranscribeAsync(
            CurrentSession.IngestedMediaPath, 
            transcriptPath);

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
            StatusMessage = $"Transcribed {result.Segments.Count} segments ({result.Language}). Ready for translation.",
        };

        _log.Info($"Transcription complete: {result.Segments.Count} segments, language: {result.Language}");
        SaveCurrentSession();
    }

    public async Task TranslateTranscriptAsync(string? targetLanguage = null, string? sourceLanguage = null)
    {
        if (string.IsNullOrEmpty(CurrentSession.TranscriptPath))
        {
            throw new InvalidOperationException("No transcript available. Please transcribe media first.");
        }

        if (!File.Exists(CurrentSession.TranscriptPath))
        {
            throw new FileNotFoundException($"Transcript file not found: {CurrentSession.TranscriptPath}");
        }

        var lang = targetLanguage ?? CurrentSettings.TargetLanguage;
        var src = sourceLanguage ?? CurrentSession.SourceLanguage ?? "auto";

        _translationService ??= new TranslationService(_log);

        var sessionDir = GetSessionDirectory();
        var translationDir = Path.Combine(sessionDir, "translations");
        Directory.CreateDirectory(translationDir);

        var fileName = Path.GetFileNameWithoutExtension(CurrentSession.TranscriptPath);
        var translationPath = Path.Combine(translationDir, $"{fileName}_{lang}.json");

        _log.Info($"Starting translation: {CurrentSession.TranscriptPath} ({src} -> {lang})");

        var result = await _translationService.TranslateAsync(
            CurrentSession.TranscriptPath,
            translationPath,
            src,
            lang);

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
            StatusMessage = $"Translated {result.Segments.Count} segments to {lang}. Ready for TTS/dubbing.",
        };

        _log.Info($"Translation complete: {result.Segments.Count} segments, {src} -> {lang}");
        SaveCurrentSession();
    }

    public async Task GenerateTtsAsync(string? voice = null)
    {
        if (string.IsNullOrEmpty(CurrentSession.TranslationPath))
        {
            throw new InvalidOperationException("No translation available. Please translate first.");
        }

        if (!File.Exists(CurrentSession.TranslationPath))
        {
            throw new FileNotFoundException($"Translation file not found: {CurrentSession.TranslationPath}");
        }

        var v = voice ?? CurrentSettings.TtsVoice;

        _ttsService ??= new TtsService(_log);

        var sessionDir = GetSessionDirectory();
        var ttsDir = Path.Combine(sessionDir, "tts");
        Directory.CreateDirectory(ttsDir);

        var fileName = Path.GetFileNameWithoutExtension(CurrentSession.TranslationPath);
        var ttsPath = Path.Combine(ttsDir, $"{fileName}_{v}.mp3");

        _log.Info($"Starting TTS generation: {CurrentSession.TranslationPath} -> {ttsPath}");

        var result = await _ttsService.GenerateTtsAsync(
            CurrentSession.TranslationPath,
            ttsPath,
            v);

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
        try
        {
            var translationJson = await File.ReadAllTextAsync(CurrentSession.TranslationPath);
            var translationData = JsonSerializer.Deserialize<JsonElement>(translationJson);
            // NOTE: "segments", "id", "translatedText" — these property names are part of an explicit
            // Python/C# artifact contract. Must match TranslationService.py output exactly.
            var segments = translationData.GetProperty("segments");

            foreach (var seg in segments.EnumerateArray())
            {
                var id = seg.GetProperty("id").GetString();
                var textProp = seg.GetProperty("translatedText");
                var text = textProp.ValueKind == JsonValueKind.String ? textProp.GetString() : null;

                if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(id))
                {
                    _log.Info($"Skipping segment {id}: empty text or ID");
                    continue;
                }

                var segmentAudioPath = Path.Combine(segmentsDir, $"{id}.mp3");

                _log.Info($"Generating TTS for segment {id}: {text.Substring(0, Math.Min(30, text.Length))}...");

                try
                {
                    var segResult = await _ttsService.GenerateSegmentTtsAsync(text, segmentAudioPath, v);

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

        var nowUtc = DateTimeOffset.UtcNow;
        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TtsPath = ttsPath,
            TtsVoice = v,
            TtsGeneratedAtUtc = nowUtc,
            TtsSegmentsPath = segmentsDir,
            TtsSegmentAudioPaths = segmentAudioPaths,
            StatusMessage = $"TTS generated ({v}). Dubbing complete.",
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

        var translationJson = await File.ReadAllTextAsync(CurrentSession.TranslationPath);
        var translationData = JsonSerializer.Deserialize<JsonElement>(translationJson);
        
        // NOTE: "segments", "id", "translatedText" — Python/C# artifact contract.
        var segments = translationData.GetProperty("segments");
        string? segmentText = null;
        
        foreach (var seg in segments.EnumerateArray())
        {
            var id = seg.GetProperty("id").GetString();
            if (id == segmentId)
            {
                var textProp = seg.GetProperty("translatedText");
                segmentText = textProp.ValueKind == JsonValueKind.String ? textProp.GetString() : null;
                break;
            }
        }

        if (string.IsNullOrEmpty(segmentText))
        {
            throw new InvalidOperationException($"Segment not found: {segmentId}");
        }

        _ttsService ??= new TtsService(_log);

        var sessionDir = GetSessionDirectory();
        var segmentsDir = Path.Combine(sessionDir, "tts", "segments");
        Directory.CreateDirectory(segmentsDir);

        var segmentAudioPath = Path.Combine(segmentsDir, $"{segmentId}.mp3");

        _log.Info($"Regenerating TTS for segment {segmentId}: {segmentText.Substring(0, Math.Min(30, segmentText.Length))}...");

        var result = await _ttsService.GenerateSegmentTtsAsync(
            segmentText,
            segmentAudioPath,
            CurrentSession.TtsVoice ?? CurrentSettings.TtsVoice);

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

        var translationJson = await File.ReadAllTextAsync(CurrentSession.TranslationPath);
        var translationData = JsonSerializer.Deserialize<JsonElement>(translationJson);
        
        // NOTE: "segments", "id", "text" — Python/C# artifact contract.
        var segments = translationData.GetProperty("segments");
        string? sourceText = null;
        
        foreach (var seg in segments.EnumerateArray())
        {
            var id = seg.GetProperty("id").GetString();
            if (id == segmentId)
            {
                var textProp = seg.GetProperty("text");
                sourceText = textProp.ValueKind == JsonValueKind.String ? textProp.GetString() : null;
                break;
            }
        }

        if (string.IsNullOrEmpty(sourceText))
        {
            throw new InvalidOperationException($"Source text not found for segment: {segmentId}");
        }

        _translationService ??= new TranslationService(_log);

        var sourceLanguage = CurrentSession.SourceLanguage ?? "es";
        var targetLanguage = CurrentSession.TargetLanguage ?? "en";

        _log.Info($"Regenerating translation for segment {segmentId}: {sourceText.Substring(0, Math.Min(30, sourceText.Length))}...");

        var result = await _translationService.TranslateSingleSegmentAsync(
            sourceText,
            segmentId,
            CurrentSession.TranslationPath,
            CurrentSession.TranslationPath,
            sourceLanguage,
            targetLanguage);

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
        if (string.IsNullOrEmpty(CurrentSession.TranscriptPath) || !File.Exists(CurrentSession.TranscriptPath))
        {
            return new List<WorkflowSegmentState>();
        }

        var segments = new List<WorkflowSegmentState>();

        // NOTE: Transcript JSON format (from TranscriptionService) — Python/C# artifact contract:
        // "segments", "start", "end", "text"
        var transcriptJson = await File.ReadAllTextAsync(CurrentSession.TranscriptPath);
        var transcriptData = JsonSerializer.Deserialize<JsonElement>(transcriptJson);
        var transcriptSegments = transcriptData.GetProperty("segments");

        var ttsSegmentPaths = CurrentSession.TtsSegmentAudioPaths;

        // NOTE: Translation JSON format (from TranslationService) — Python/C# artifact contract:
        // "segments", "id", "text", "translatedText"
        Dictionary<string, string>? translationTexts = null;
        if (!string.IsNullOrEmpty(CurrentSession.TranslationPath) && File.Exists(CurrentSession.TranslationPath))
        {
            translationTexts = new Dictionary<string, string>();
            var translationJson = await File.ReadAllTextAsync(CurrentSession.TranslationPath);
            var translationData = JsonSerializer.Deserialize<JsonElement>(translationJson);
            var translationSegments = translationData.GetProperty("segments");

            foreach (var seg in translationSegments.EnumerateArray())
            {
                var id = seg.GetProperty("id").GetString();
                if (id != null)
                {
                    var textProp = seg.GetProperty("translatedText");
                    var text = textProp.ValueKind == JsonValueKind.String ? textProp.GetString() : null;
                    // Only count non-empty translations; empty string means the translation call failed.
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        translationTexts[id] = text;
                    }
                }
            }
        }

        foreach (var seg in transcriptSegments.EnumerateArray())
        {
            var start = seg.GetProperty("start").GetDouble();
            var id = SegmentId(start);

            var end = seg.GetProperty("end").GetDouble();

            var textProp = seg.GetProperty("text");
            var text = textProp.ValueKind == JsonValueKind.String ? textProp.GetString() ?? "" : "";

            string? translatedText = null;
            var hasTranslation = translationTexts != null && translationTexts.TryGetValue(id, out translatedText);
            var hasTtsAudio = ttsSegmentPaths != null
                && ttsSegmentPaths.TryGetValue(id, out var audioPath)
                && File.Exists(audioPath);

            segments.Add(new WorkflowSegmentState(
                id,
                start,
                end,
                text,
                hasTranslation,
                translatedText,
                hasTtsAudio));
        }

        return segments;
    }

    // Stable segment ID derived from start time — must match the format written by TranslationService.
    // Python: f"segment_{start}" → e.g. "segment_0.0", "segment_3.68"
    internal static string SegmentId(double start) =>
        start == (int)start
            ? FormattableString.Invariant($"segment_{start:0.0}")
            : FormattableString.Invariant($"segment_{start}");

    private string GetSessionDirectory() => SessionDirectoryFor(CurrentSession.SessionId);

    private static string SessionDirectoryFor(Guid sessionId)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "BabelPlayer", "sessions", sessionId.ToString());
    }

    /// <summary>
    /// Restores a previously-opened session by ID, stashing the current one first.
    /// The caller is responsible for reloading the video transport after this returns.
    /// </summary>
    public void RestoreSession(Guid sessionId)
    {
        // Try in-memory cache first, then fall back to disk.
        WorkflowSessionSnapshot? restored =
            _mediaSnapshotCache.Values.FirstOrDefault(s => s.SessionId == sessionId)
            ?? _perSessionStore.Load(sessionId);

        if (restored is null)
        {
            _log.Warning($"RestoreSession: session {sessionId} not found in cache or on disk.");
            return;
        }

        // Stash and persist the current session before switching.
        if (!string.IsNullOrEmpty(CurrentSession.SourceMediaPath))
        {
            _mediaSnapshotCache[MediaKey(CurrentSession.SourceMediaPath)] = CurrentSession;
            _perSessionStore.Save(CurrentSession);
            _recentStore.Upsert(new RecentSessionEntry(
                CurrentSession.SessionId,
                CurrentSession.SourceMediaPath,
                Path.GetFileName(CurrentSession.SourceMediaPath),
                CurrentSession.Stage,
                CurrentSession.LastUpdatedAtUtc));
        }

        var validated = ValidateArtifacts(restored);
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
        SaveCurrentSession();
        RecentSessions = _recentStore.Load();
    }

    public void SaveCurrentSession()
    {
        CurrentSession = CurrentSession with { LastUpdatedAtUtc = DateTimeOffset.UtcNow };
        _store.Save(CurrentSession);
        PersistenceStatus = $"Saved current session snapshot to {StateFilePath}.";
        _log.Info(PersistenceStatus);
    }

}
