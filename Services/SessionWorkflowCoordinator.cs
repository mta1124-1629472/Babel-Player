using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Babel.Deck.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Babel.Deck.Services;

public sealed partial class SessionWorkflowCoordinator : ObservableObject
{
    private readonly SessionSnapshotStore _store;
    private readonly AppLog _log;
    private TranscriptionService? _transcriptionService;
    private TranslationService? _translationService;
    private TtsService? _ttsService;

    [ObservableProperty]
    private WorkflowSessionSnapshot _currentSession = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);

    [ObservableProperty]
    private string _sessionSource = "Session not initialized.";

    [ObservableProperty]
    private string _persistenceStatus = "Persistence has not run yet.";

    public SessionWorkflowCoordinator(SessionSnapshotStore store, AppLog log)
    {
        _store = store;
        _log = log;
    }

    public string StateFilePath => _store.StateFilePath;

    public string LogFilePath => _log.LogFilePath;

    public void Initialize()
    {
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
            bool mediaMissing = false;
            bool transcriptMissing = false;
            bool translationMissing = false;
            bool ttsMissing = false;

            if (!string.IsNullOrEmpty(snapshot.IngestedMediaPath) && 
                !File.Exists(snapshot.IngestedMediaPath))
            {
                _log.Warning($"Ingested media artifact missing: {snapshot.IngestedMediaPath}");
                mediaMissing = true;
            }

            if (!string.IsNullOrEmpty(snapshot.TranscriptPath) && 
                !File.Exists(snapshot.TranscriptPath))
            {
                _log.Warning($"Transcript artifact missing: {snapshot.TranscriptPath}");
                transcriptMissing = true;
            }

            if (!string.IsNullOrEmpty(snapshot.TranslationPath) && 
                !File.Exists(snapshot.TranslationPath))
            {
                _log.Warning($"Translation artifact missing: {snapshot.TranslationPath}");
                translationMissing = true;
            }

            if (!string.IsNullOrEmpty(snapshot.TtsPath) && 
                !File.Exists(snapshot.TtsPath))
            {
                _log.Warning($"TTS artifact missing: {snapshot.TtsPath}");
                ttsMissing = true;
            }

            string statusMessage;
            if (mediaMissing && transcriptMissing && translationMissing && ttsMissing)
            {
                statusMessage = "Session had all artifacts but they are missing. Please restart the workflow.";
            }
            else if (ttsMissing && snapshot.Stage >= SessionWorkflowStage.TtsGenerated)
            {
                statusMessage = "Session had TTS but artifact is missing. Please regenerate TTS.";
            }
            else if (translationMissing && snapshot.Stage >= SessionWorkflowStage.Translated)
            {
                statusMessage = "Session had translation but artifact is missing. Please re-translate.";
            }
            else if (transcriptMissing && snapshot.Stage >= SessionWorkflowStage.Transcribed)
            {
                statusMessage = "Session had transcript but artifact is missing. Please re-transcribe.";
            }
            else if (mediaMissing)
            {
                statusMessage = "Session had media but artifact is missing. Please re-load media.";
            }
            else
            {
                statusMessage = snapshot.Stage >= SessionWorkflowStage.TtsGenerated
                    ? "Resumed session with TTS. Dubbing complete."
                    : snapshot.Stage >= SessionWorkflowStage.Translated
                        ? "Resumed session with translation. Ready for TTS/dubbing."
                        : snapshot.Stage >= SessionWorkflowStage.Transcribed
                            ? "Resumed session with transcript. Ready for translation."
                            : "Resumed saved foundation session. Downstream workflow milestones are still not implemented.";
            }

            CurrentSession = snapshot with
            {
                LastUpdatedAtUtc = nowUtc,
                StatusMessage = statusMessage,
            };

            if (mediaMissing && transcriptMissing && translationMissing && ttsMissing)
            {
                SessionSource = "Resumed session but all artifacts are missing.";
            }
            else if (ttsMissing && snapshot.Stage >= SessionWorkflowStage.TtsGenerated)
            {
                SessionSource = "Resumed session but TTS artifact is missing.";
            }
            else if (translationMissing && snapshot.Stage >= SessionWorkflowStage.Translated)
            {
                SessionSource = "Resumed session but translation artifact is missing.";
            }
            else if (transcriptMissing && snapshot.Stage >= SessionWorkflowStage.Transcribed)
            {
                SessionSource = "Resumed session but transcript artifact is missing.";
            }
            else if (mediaMissing)
            {
                SessionSource = "Resumed session but media artifact is missing.";
            }
            else
            {
                SessionSource = snapshot.Stage >= SessionWorkflowStage.TtsGenerated
                    ? "Resumed session with TTS."
                    : snapshot.Stage >= SessionWorkflowStage.Translated
                        ? "Resumed session with translation."
                        : snapshot.Stage >= SessionWorkflowStage.Transcribed
                            ? "Resumed session with transcript."
                            : "Resumed the saved foundation session.";
            }
        }

        PersistenceStatus = loadResult.StatusMessage;
        _log.Info(SessionSource);
        SaveCurrentSession();
    }

    public void LoadMedia(string sourceMediaPath)
    {
        if (!File.Exists(sourceMediaPath))
        {
            throw new FileNotFoundException($"Source media file not found: {sourceMediaPath}");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var sessionDir = GetSessionDirectory();
        var mediaDir = Path.Combine(sessionDir, "media");
        Directory.CreateDirectory(mediaDir);

        var fileName = Path.GetFileName(sourceMediaPath);
        var ingestedPath = Path.Combine(mediaDir, fileName);

        File.Copy(sourceMediaPath, ingestedPath, overwrite: true);
        _log.Info($"Copied media to session artifact: {ingestedPath}");

        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.MediaLoaded,
            SourceMediaPath = sourceMediaPath,
            IngestedMediaPath = ingestedPath,
            MediaLoadedAtUtc = nowUtc,
            StatusMessage = "Media loaded. Ready for transcription.",
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
            TranscribedAtUtc = nowUtc,
            StatusMessage = $"Transcribed {result.Segments.Count} segments. Ready for translation.",
        };

        _log.Info($"Transcription complete: {result.Segments.Count} segments, language: {result.Language}");
        SaveCurrentSession();
    }

    public async Task TranslateTranscriptAsync(string targetLanguage = "en", string sourceLanguage = "es")
    {
        if (string.IsNullOrEmpty(CurrentSession.TranscriptPath))
        {
            throw new InvalidOperationException("No transcript available. Please transcribe media first.");
        }

        if (!File.Exists(CurrentSession.TranscriptPath))
        {
            throw new FileNotFoundException($"Transcript file not found: {CurrentSession.TranscriptPath}");
        }

        _translationService ??= new TranslationService(_log);

        var sessionDir = GetSessionDirectory();
        var translationDir = Path.Combine(sessionDir, "translations");
        Directory.CreateDirectory(translationDir);

        var fileName = Path.GetFileNameWithoutExtension(CurrentSession.TranscriptPath);
        var translationPath = Path.Combine(translationDir, $"{fileName}_{targetLanguage}.json");

        _log.Info($"Starting translation: {CurrentSession.TranscriptPath} ({sourceLanguage} -> {targetLanguage})");

        var result = await _translationService.TranslateAsync(
            CurrentSession.TranscriptPath,
            translationPath,
            sourceLanguage,
            targetLanguage);

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
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            TranslatedAtUtc = nowUtc,
            StatusMessage = $"Translated {result.Segments.Count} segments to {targetLanguage}. Ready for TTS/dubbing.",
        };

        _log.Info($"Translation complete: {result.Segments.Count} segments, {sourceLanguage} -> {targetLanguage}");
        SaveCurrentSession();
    }

    public async Task GenerateTtsAsync(string voice = "en-US-AriaNeural")
    {
        if (string.IsNullOrEmpty(CurrentSession.TranslationPath))
        {
            throw new InvalidOperationException("No translation available. Please translate first.");
        }

        if (!File.Exists(CurrentSession.TranslationPath))
        {
            throw new FileNotFoundException($"Translation file not found: {CurrentSession.TranslationPath}");
        }

        _ttsService ??= new TtsService(_log);

        var sessionDir = GetSessionDirectory();
        var ttsDir = Path.Combine(sessionDir, "tts");
        Directory.CreateDirectory(ttsDir);

        var fileName = Path.GetFileNameWithoutExtension(CurrentSession.TranslationPath);
        var ttsPath = Path.Combine(ttsDir, $"{fileName}_{voice}.mp3");

        _log.Info($"Starting TTS generation: {CurrentSession.TranslationPath} -> {ttsPath}");

        var result = await _ttsService.GenerateTtsAsync(
            CurrentSession.TranslationPath,
            ttsPath,
            voice);

        if (!result.Success)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown TTS error";
            _log.Error($"TTS failed: {errorMsg}", new Exception(errorMsg));
            throw new InvalidOperationException($"TTS failed: {errorMsg}");
        }

        _log.Info($"TTS complete: {ttsPath}, size: {result.FileSizeBytes} bytes");

        var segmentsDir = Path.Combine(ttsDir, "segments");
        Directory.CreateDirectory(segmentsDir);

        var nowUtc = DateTimeOffset.UtcNow;
        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TtsPath = ttsPath,
            TtsVoice = voice,
            TtsGeneratedAtUtc = nowUtc,
            TtsSegmentsPath = segmentsDir,
            TtsSegmentAudioPaths = new Dictionary<string, string>(),
            StatusMessage = $"TTS generated ({voice}). Dubbing complete.",
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
            CurrentSession.TtsVoice ?? "en-US-AriaNeural");

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

    public List<WorkflowSegmentState> GetSegmentWorkflowList()
    {
        if (string.IsNullOrEmpty(CurrentSession.TranscriptPath) || !File.Exists(CurrentSession.TranscriptPath))
        {
            return new List<WorkflowSegmentState>();
        }

        var segments = new List<WorkflowSegmentState>();

        var transcriptJson = File.ReadAllText(CurrentSession.TranscriptPath);
        var transcriptData = JsonSerializer.Deserialize<JsonElement>(transcriptJson);
        var transcriptSegments = transcriptData.GetProperty("segments");

        Dictionary<string, string>? ttsSegmentPaths = null;
        if (CurrentSession.TtsSegmentAudioPaths != null)
        {
            ttsSegmentPaths = CurrentSession.TtsSegmentAudioPaths;
        }

        Dictionary<string, string>? translationTexts = null;
        if (!string.IsNullOrEmpty(CurrentSession.TranslationPath) && File.Exists(CurrentSession.TranslationPath))
        {
            translationTexts = new Dictionary<string, string>();
            var translationJson = File.ReadAllText(CurrentSession.TranslationPath);
            var translationData = JsonSerializer.Deserialize<JsonElement>(translationJson);
            var translationSegments = translationData.GetProperty("segments");
            
            foreach (var seg in translationSegments.EnumerateArray())
            {
                var id = seg.GetProperty("id").GetString();
                if (id != null)
                {
                    var textProp = seg.GetProperty("translatedText");
                    var text = textProp.ValueKind == JsonValueKind.String ? textProp.GetString() : null;
                    if (text != null)
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

            var hasTranslation = translationTexts != null && translationTexts.ContainsKey(id);
            var translatedText = hasTranslation ? translationTexts[id] : null;
            var hasTtsAudio = ttsSegmentPaths != null && ttsSegmentPaths.ContainsKey(id);

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

    private string GetSessionDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "BabelDeck", "sessions", CurrentSession.SessionId.ToString());
    }

    public void SaveCurrentSession()
    {
        CurrentSession = CurrentSession with { LastUpdatedAtUtc = DateTimeOffset.UtcNow };
        _store.Save(CurrentSession);
        PersistenceStatus = $"Saved current session snapshot to {StateFilePath}.";
        _log.Info(PersistenceStatus);
    }
}
