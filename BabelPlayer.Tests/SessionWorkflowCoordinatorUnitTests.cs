using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for SessionWorkflowCoordinator that do not require Python, FFmpeg, or libmpv.
/// These tests exercise pure coordination logic: Initialize, LoadMedia, Reset*, InjectTestTranscript,
/// UpdateSettings, CheckSettingsInvalidation, NotifySettingsModified, SegmentId, and SaveCurrentSession.
/// </summary>
public sealed class SessionWorkflowCoordinatorUnitTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;
    private readonly SessionSnapshotStore _store;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;
    private readonly AppSettings _settings;

    // A small real media file that exists in the test output directory
    private readonly string _mediaPath;

    public SessionWorkflowCoordinatorUnitTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-coord-unit-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _store = new SessionSnapshotStore(Path.Combine(_dir, "session.json"), _log);
        _perSessionStore = new PerSessionSnapshotStore(Path.Combine(_dir, "sessions"), _log);
        _recentStore = new RecentSessionsStore(Path.Combine(_dir, "recent-sessions.json"), _log);
        _settings = new AppSettings();

        // The test media (sample.mp4) is copied to the output dir by the .csproj
        _mediaPath = Path.Combine(AppContext.BaseDirectory, "test-assets", "video", "sample.mp4");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private SessionWorkflowCoordinator CreateCoordinator(
        AppSettings? settings = null,
        IContainerizedInferenceManager? containerizedInferenceManager = null) =>
        new SessionWorkflowCoordinator(
            _store,
            _log,
            settings ?? _settings,
            _perSessionStore,
            _recentStore,
            new TranscriptionRegistry(_log),
            new TranslationRegistry(_log),
            new TtsRegistry(_log),
            containerizedInferenceManager: containerizedInferenceManager);

    private AppSettings CreateMatchingSettings() =>
        new()
        {
            TranscriptionRuntime = _settings.TranscriptionRuntime,
            TranscriptionProvider = _settings.TranscriptionProvider,
            TranscriptionModel = _settings.TranscriptionModel,
            TranscriptionCpuComputeType = _settings.TranscriptionCpuComputeType,
            TranscriptionCpuThreads = _settings.TranscriptionCpuThreads,
            TranscriptionNumWorkers = _settings.TranscriptionNumWorkers,
            TranslationRuntime = _settings.TranslationRuntime,
            TranslationProvider = _settings.TranslationProvider,
            TranslationModel = _settings.TranslationModel,
            TtsRuntime = _settings.TtsRuntime,
            TtsProvider = _settings.TtsProvider,
            TtsVoice = _settings.TtsVoice,
            TargetLanguage = _settings.TargetLanguage,
            PiperModelDir = _settings.PiperModelDir,
            ContainerizedServiceUrl = _settings.ContainerizedServiceUrl,
            AlwaysRunContainerAtAppStart = _settings.AlwaysRunContainerAtAppStart,
            VideoHwdec = _settings.VideoHwdec,
            VideoGpuApi = _settings.VideoGpuApi,
            VideoUseGpuNext = _settings.VideoUseGpuNext,
            VideoVsrEnabled = _settings.VideoVsrEnabled,
            VideoVsrQuality = _settings.VideoVsrQuality,
            VideoHdrEnabled = _settings.VideoHdrEnabled,
            VideoToneMapping = _settings.VideoToneMapping,
            VideoTargetPeak = _settings.VideoTargetPeak,
            VideoHdrComputePeak = _settings.VideoHdrComputePeak,
            VideoExportEncoder = _settings.VideoExportEncoder,
            Theme = _settings.Theme,
            MaxRecentSessions = _settings.MaxRecentSessions,
            AutoSaveEnabled = _settings.AutoSaveEnabled,
        };

    // ── Initialize ─────────────────────────────────────────────────────────────

    [Fact]
    public void Initialize_NoSavedSnapshot_CreatesFoundationSession()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        Assert.Equal(SessionWorkflowStage.Foundation, coord.CurrentSession.Stage);
    }

    [Fact]
    public void Initialize_NoSavedSnapshot_SessionSourceMentionsNewSession()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        Assert.False(string.IsNullOrWhiteSpace(coord.SessionSource));
    }

    [Fact]
    public void Initialize_WithSavedSnapshot_RestoresSession()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        _store.Save(snapshot);

        var coord = CreateCoordinator();
        coord.Initialize();

        Assert.Equal(snapshot.SessionId, coord.CurrentSession.SessionId);
    }

    [Fact]
    public void Initialize_LoadsRecentSessions()
    {
        var entry = new RecentSessionEntry(
            Guid.NewGuid(),
            "/video/test.mp4",
            "test.mp4",
            SessionWorkflowStage.MediaLoaded,
            DateTimeOffset.UtcNow);
        _recentStore.Upsert(entry);

        var coord = CreateCoordinator();
        coord.Initialize();

        Assert.NotEmpty(coord.RecentSessions);
    }

    [Fact]
    public void Initialize_PersistenceStatusIsNotEmpty()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        Assert.False(string.IsNullOrWhiteSpace(coord.PersistenceStatus));
    }

    // ── LoadMedia ──────────────────────────────────────────────────────────────

    [Fact]
    public void LoadMedia_FileNotFound_ThrowsFileNotFoundException()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        Assert.Throws<FileNotFoundException>(() => coord.LoadMedia("/nonexistent/path/video.mp4"));
    }

    [Fact]
    public void LoadMedia_ValidFile_AdvancesToMediaLoadedStage()
    {
        if (!File.Exists(_mediaPath)) return; // skip if media not present

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);
        Assert.Equal(SessionWorkflowStage.MediaLoaded, coord.CurrentSession.Stage);
    }

    [Fact]
    public void LoadMedia_ValidFile_SetsSourceMediaPath()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);
        Assert.Equal(_mediaPath, coord.CurrentSession.SourceMediaPath);
    }

    [Fact]
    public void LoadMedia_ValidFile_CopiesIngestedMedia()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);

        Assert.NotNull(coord.CurrentSession.IngestedMediaPath);
        Assert.True(File.Exists(coord.CurrentSession.IngestedMediaPath));
    }

    [Fact]
    public void LoadMedia_ValidFile_StatusMessageIndicatesReady()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);
        Assert.Contains("transcription", coord.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadMedia_ValidFile_QueuesNonAutoPlayMediaReloadRequest()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);

        var request = coord.ConsumePendingMediaReloadRequest();
        Assert.NotNull(request);
        Assert.False(request!.AutoPlay);
        Assert.Equal(coord.CurrentSession.IngestedMediaPath, request.IngestedMediaPath);
    }

    // ── ResetPipeline* ─────────────────────────────────────────────────────────

    [Fact]
    public void ResetPipelineToMediaLoaded_WhenAtFoundation_IsNoOp()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        // Stage is Foundation — reset should be a no-op
        coord.ResetPipelineToMediaLoaded();
        Assert.Equal(SessionWorkflowStage.Foundation, coord.CurrentSession.Stage);
    }

    [Fact]
    public void ResetPipelineToMediaLoaded_WhenAtMediaLoaded_ClearsDownstreamFields()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);

        // Manually inject some downstream fields to verify they get cleared
        coord.InjectTestTranscript(
            CreateTempFile("{\"language\":\"es\",\"segments\":[]}"),
            CreateTempFile("{\"segments\":[]}"));

        coord.ResetPipelineToMediaLoaded();

        Assert.Equal(SessionWorkflowStage.MediaLoaded, coord.CurrentSession.Stage);
        Assert.Null(coord.CurrentSession.TranscriptPath);
        Assert.Null(coord.CurrentSession.TranslationPath);
        Assert.Null(coord.CurrentSession.TtsPath);
        Assert.Null(coord.CurrentSession.SourceLanguage);
        Assert.Null(coord.CurrentSession.TargetLanguage);
    }

    [Fact]
    public void ResetPipelineToMediaLoaded_ClearsAllArtifactProvenance()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[]}");
        var ttsPath = CreateTempFile("fake audio");

        _store.Save(coord.CurrentSession with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranslationPath = translationPath,
            TranslationProvider = _settings.TranslationProvider,
            TranslationModel = _settings.TranslationModel,
            TargetLanguage = _settings.TargetLanguage,
            TranslatedAtUtc = DateTimeOffset.UtcNow,
            TtsPath = ttsPath,
            TtsProvider = _settings.TtsProvider,
            TtsVoice = _settings.TtsVoice,
            TtsGeneratedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _settings.TranscriptionProvider,
            TranscriptionModel = _settings.TranscriptionModel,
        });

        coord = CreateCoordinator();
        coord.Initialize();
        coord.ResetPipelineToMediaLoaded();

        Assert.Null(coord.CurrentSession.TranscriptPath);
        Assert.Null(coord.CurrentSession.TranslationPath);
        Assert.Null(coord.CurrentSession.TtsPath);
        Assert.Null(coord.CurrentSession.TranscriptionProvider);
        Assert.Null(coord.CurrentSession.TranscriptionModel);
        Assert.Null(coord.CurrentSession.TranslationProvider);
        Assert.Null(coord.CurrentSession.TranslationModel);
        Assert.Null(coord.CurrentSession.TtsProvider);
        Assert.Null(coord.CurrentSession.TtsVoice);
    }

    [Fact]
    public void ResetPipelineToTranscribed_PreservesTranscriptionProvenanceAndClearsDownstream()
    {
        if (!File.Exists(_mediaPath)) return;

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[]}");

        _store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Translated,
            SourceMediaPath = _mediaPath,
            IngestedMediaPath = _mediaPath,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranslationPath = translationPath,
            TargetLanguage = _settings.TargetLanguage,
            TranslatedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _settings.TranscriptionProvider,
            TranscriptionModel = _settings.TranscriptionModel,
            TranslationProvider = _settings.TranslationProvider,
            TranslationModel = _settings.TranslationModel,
            TtsProvider = _settings.TtsProvider,
            TtsVoice = _settings.TtsVoice,
        });

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.ResetPipelineToTranscribed();

        Assert.Equal(SessionWorkflowStage.Transcribed, coord.CurrentSession.Stage);
        Assert.Equal(_settings.TranscriptionProvider, coord.CurrentSession.TranscriptionProvider);
        Assert.Equal(_settings.TranscriptionModel, coord.CurrentSession.TranscriptionModel);
        Assert.Null(coord.CurrentSession.TranslationProvider);
        Assert.Null(coord.CurrentSession.TranslationModel);
        Assert.Null(coord.CurrentSession.TtsProvider);
        Assert.Null(coord.CurrentSession.TtsVoice);
    }

    [Fact]
    public void ResetPipelineToTranscribed_WhenAtFoundation_IsNoOp()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        coord.ResetPipelineToTranscribed();
        // Stage is Foundation (< Transcribed) — no change
        Assert.Equal(SessionWorkflowStage.Foundation, coord.CurrentSession.Stage);
    }

    [Fact]
    public void ResetPipelineToTranscribed_WhenAtTranslated_ClearsTranslationAndTts()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"segments\":[]}");
        coord.InjectTestTranscript(transcriptPath, translationPath);

        // The session is now at Translated stage
        coord.ResetPipelineToTranscribed();

        Assert.Equal(SessionWorkflowStage.Transcribed, coord.CurrentSession.Stage);
        Assert.Null(coord.CurrentSession.TranslationPath);
        Assert.Null(coord.CurrentSession.TargetLanguage);
        Assert.Null(coord.CurrentSession.TtsPath);
        // Transcript should be intact
        Assert.Equal(transcriptPath, coord.CurrentSession.TranscriptPath);
    }

    [Fact]
    public void ResetPipelineToTranslated_WhenAtFoundation_IsNoOp()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        coord.ResetPipelineToTranslated();
        Assert.Equal(SessionWorkflowStage.Foundation, coord.CurrentSession.Stage);
    }

    // ── InjectTestTranscript ──────────────────────────────────────────────────

    [Fact]
    public void InjectTestTranscript_WithoutTranslation_SetsTranscribedStage()
    {
        var coord = CreateCoordinator();
        coord.Initialize();

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        coord.InjectTestTranscript(transcriptPath);

        Assert.Equal(SessionWorkflowStage.Transcribed, coord.CurrentSession.Stage);
        Assert.Equal(transcriptPath, coord.CurrentSession.TranscriptPath);
    }

    [Fact]
    public void InjectTestTranscript_WithTranslation_SetsTranslatedStage()
    {
        var coord = CreateCoordinator();
        coord.Initialize();

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"segments\":[]}");
        coord.InjectTestTranscript(transcriptPath, translationPath);

        Assert.Equal(SessionWorkflowStage.Translated, coord.CurrentSession.Stage);
        Assert.Equal(translationPath, coord.CurrentSession.TranslationPath);
    }

    [Fact]
    public void InjectTestTranscript_PersistsSession()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        coord.InjectTestTranscript(transcriptPath);
        coord.FlushPendingSave();

        // Verify the snapshot was written to disk with Transcribed stage
        var loaded = _store.Load().Snapshot;
        Assert.NotNull(loaded);
        Assert.Equal(SessionWorkflowStage.Transcribed, loaded.Stage);
        Assert.Equal(transcriptPath, loaded.TranscriptPath);
    }

    // ── UpdateSettings ────────────────────────────────────────────────────────

    [Fact]
    public void UpdateSettings_SameSettings_DoesNotRaiseSettingsModified()
    {
        var coord = CreateCoordinator();
        coord.Initialize();

        bool raised = false;
        coord.SettingsModified += () => raised = true;

        // UpdateSettings itself does not raise SettingsModified — that's the caller's job
        coord.UpdateSettings(_settings);
        Assert.False(raised);
    }

    [Fact]
    public void UpdateSettings_ChangedSettings_UpdatesCurrentSettings()
    {
        var coord = CreateCoordinator();
        coord.Initialize();

        var newSettings = new AppSettings { TtsVoice = "de-DE-KatjaNeural" };
        coord.UpdateSettings(newSettings);

        Assert.Equal("de-DE-KatjaNeural", coord.CurrentSettings.TtsVoice);
    }

    // ── NotifySettingsModified ────────────────────────────────────────────────

    [Fact]
    public void NotifySettingsModified_RaisesSettingsModifiedEvent()
    {
        var coord = CreateCoordinator();
        coord.Initialize();

        bool raised = false;
        coord.SettingsModified += () => raised = true;
        coord.NotifySettingsModified();

        Assert.True(raised);
    }

    [Fact]
    public void NotifySettingsModified_NoSubscribers_DoesNotThrow()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        // Should not throw even with no subscribers
        coord.NotifySettingsModified();
    }

    // ── Multi-speaker mapping APIs ───────────────────────────────────────────

    [Fact]
    public void SetSpeakerVoiceAssignment_PersistsInCurrentSession()
    {
        var coord = CreateCoordinator();
        coord.Initialize();

        coord.SetSpeakerVoiceAssignment("spk_01", "en-US-AriaNeural");

        Assert.NotNull(coord.CurrentSession.SpeakerVoiceAssignments);
        Assert.True(coord.CurrentSession.SpeakerVoiceAssignments!.TryGetValue("spk_01", out var voice));
        Assert.Equal("en-US-AriaNeural", voice);
    }

    [Fact]
    public void RemoveSpeakerVoiceAssignment_RemovesEntry()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        coord.SetSpeakerVoiceAssignment("spk_01", "en-US-AriaNeural");

        coord.RemoveSpeakerVoiceAssignment("spk_01");

        Assert.True(coord.CurrentSession.SpeakerVoiceAssignments is null
            || !coord.CurrentSession.SpeakerVoiceAssignments.ContainsKey("spk_01"));
    }

    [Fact]
    public void SetSpeakerReferenceAudioPath_PersistsInCurrentSession()
    {
        var coord = CreateCoordinator();
        coord.Initialize();

        coord.SetSpeakerReferenceAudioPath("spk_01", "/tmp/spk_01.wav");

        Assert.NotNull(coord.CurrentSession.SpeakerReferenceAudioPaths);
        Assert.True(coord.CurrentSession.SpeakerReferenceAudioPaths!.TryGetValue("spk_01", out var path));
        Assert.Equal("/tmp/spk_01.wav", path);
    }

    [Fact]
    public void RemoveSpeakerReferenceAudioPath_RemovesEntry()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        coord.SetSpeakerReferenceAudioPath("spk_01", "/tmp/spk_01.wav");

        coord.RemoveSpeakerReferenceAudioPath("spk_01");

        Assert.True(coord.CurrentSession.SpeakerReferenceAudioPaths is null
            || !coord.CurrentSession.SpeakerReferenceAudioPaths.ContainsKey("spk_01"));
    }

    [Fact]
    public void SetMultiSpeakerEnabled_UpdatesSessionFlag()
    {
        var coord = CreateCoordinator();
        coord.Initialize();

        coord.SetMultiSpeakerEnabled(true);

        Assert.True(coord.CurrentSession.MultiSpeakerEnabled);
    }

    [Fact]
    public async Task RegenerateSegmentTts_MultiSpeakerEnabled_UsesSpeakerMappedVoice()
    {
        var fakeTts = new FakeTtsProvider();
        var fakeTtsRegistry = new FakeTtsRegistry(fakeTts);
        var coord = new SessionWorkflowCoordinator(
            _store,
            _log,
            _settings,
            _perSessionStore,
            _recentStore,
            new TranscriptionRegistry(_log),
            new TranslationRegistry(_log),
            fakeTtsRegistry);
        coord.Initialize();

        var translationPath = CreateTempFile("""
        {
          "sourceLanguage": "es",
          "targetLanguage": "en",
          "segments": [
            {
              "id": "segment_0.0",
              "start": 0.0,
              "end": 1.0,
              "text": "hola",
              "translatedText": "hello",
              "speakerId": "spk_01"
            }
          ]
        }
        """);

        coord.CurrentSession = coord.CurrentSession with
        {
            Stage = SessionWorkflowStage.Translated,
            TranslationPath = translationPath,
            TtsVoice = "global-voice",
            MultiSpeakerEnabled = true,
            SpeakerVoiceAssignments = new Dictionary<string, string> { ["spk_01"] = "mapped-voice" },
        };

        await coord.RegenerateSegmentTtsAsync("segment_0.0");

        Assert.NotNull(fakeTts.LastSegmentRequest);
        Assert.Equal("mapped-voice", fakeTts.LastSegmentRequest!.VoiceName);
        Assert.Equal("spk_01", fakeTts.LastSegmentRequest.SpeakerId);
    }

    [Fact]
    public async Task RegenerateSegmentTts_MultiSpeakerEnabled_UsesDefaultFallbackWhenSpeakerUnmapped()
    {
        var fakeTts = new FakeTtsProvider();
        var fakeTtsRegistry = new FakeTtsRegistry(fakeTts);
        var coord = new SessionWorkflowCoordinator(
            _store,
            _log,
            _settings,
            _perSessionStore,
            _recentStore,
            new TranscriptionRegistry(_log),
            new TranslationRegistry(_log),
            fakeTtsRegistry);
        coord.Initialize();

        var translationPath = CreateTempFile("""
        {
          "sourceLanguage": "es",
          "targetLanguage": "en",
          "segments": [
            {
              "id": "segment_0.0",
              "start": 0.0,
              "end": 1.0,
              "text": "hola",
              "translatedText": "hello",
              "speakerId": "spk_02"
            }
          ]
        }
        """);

        coord.CurrentSession = coord.CurrentSession with
        {
            Stage = SessionWorkflowStage.Translated,
            TranslationPath = translationPath,
            TtsVoice = "global-voice",
            MultiSpeakerEnabled = true,
            DefaultTtsVoiceFallback = "fallback-voice",
        };

        await coord.RegenerateSegmentTtsAsync("segment_0.0");

        Assert.NotNull(fakeTts.LastSegmentRequest);
        Assert.Equal("fallback-voice", fakeTts.LastSegmentRequest!.VoiceName);
        Assert.Equal("spk_02", fakeTts.LastSegmentRequest.SpeakerId);
    }

    [Fact]
    public async Task RegenerateSegmentTts_SingleSpeakerXtts_UsesDefaultReferenceClip()
    {
        var fakeTts = new FakeTtsProvider();
        var fakeTtsRegistry = new FakeTtsRegistry(fakeTts);
        var settings = CreateMatchingSettings();
        settings.TtsProvider = ProviderNames.XttsContainer;

        var coord = new SessionWorkflowCoordinator(
            _store,
            _log,
            settings,
            _perSessionStore,
            _recentStore,
            new TranscriptionRegistry(_log),
            new TranslationRegistry(_log),
            fakeTtsRegistry);
        coord.Initialize();

        var translationPath = CreateTempFile("""
        {
          "sourceLanguage": "es",
          "targetLanguage": "en",
          "segments": [
            {
              "id": "segment_0.0",
              "start": 0.0,
              "end": 1.0,
              "text": "hola",
              "translatedText": "hello"
            }
          ]
        }
        """);
        var defaultRefPath = Path.Combine(_dir, "xtts-single-ref.wav");
        await File.WriteAllBytesAsync(defaultRefPath, [1, 2, 3]);

        coord.CurrentSession = coord.CurrentSession with
        {
            Stage = SessionWorkflowStage.Translated,
            TranslationPath = translationPath,
            TtsVoice = "xtts-v2",
            MultiSpeakerEnabled = false,
            SpeakerReferenceAudioPaths = new Dictionary<string, string>
            {
                [XttsReferenceKeys.SingleSpeakerDefault] = defaultRefPath,
            },
        };

        await coord.RegenerateSegmentTtsAsync("segment_0.0");

        Assert.NotNull(fakeTts.LastSegmentRequest);
        Assert.Equal(defaultRefPath, fakeTts.LastSegmentRequest!.ReferenceAudioPath);
    }

    // ── CheckSettingsInvalidation ─────────────────────────────────────────────

    [Fact]
    public void CheckSettingsInvalidation_NothingChanged_ReturnsNone()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"segments\":[]}");
        coord.InjectTestTranscript(transcriptPath, translationPath);

        // Stamp the current provider settings into the snapshot
        var session = coord.CurrentSession with
        {
            TranscriptionProvider = _settings.TranscriptionProvider,
            TranscriptionModel = _settings.TranscriptionModel,
            TranslationProvider = _settings.TranslationProvider,
            TranslationModel = _settings.TranslationModel,
            TtsProvider = _settings.TtsProvider,
            TtsVoice = _settings.TtsVoice,
            TargetLanguage = _settings.TargetLanguage,
        };
        // Directly simulate the coordinator state as if the pipeline had run with current settings
        // by updating the store and reinitialising
        _store.Save(session);
        var coord2 = CreateCoordinator();
        coord2.Initialize();

        var invalidation = coord2.CheckSettingsInvalidation();
        Assert.Equal(PipelineInvalidation.None, invalidation);
    }

    [Fact]
    public void CheckSettingsInvalidation_MediaLoaded_TranslationModelChange_ReturnsNone()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);

        var changedSettings = CreateMatchingSettings();
        changedSettings.TranslationModel = "nllb-200-1.3B";

        var coord2 = CreateCoordinator(changedSettings);
        coord2.Initialize();
        coord2.LoadMedia(_mediaPath);

        Assert.Equal(PipelineInvalidation.None, coord2.CheckSettingsInvalidation());
    }

    [Fact]
    public void CheckSettingsInvalidation_Transcribed_TranslationModelChange_ReturnsNone()
    {
        if (!File.Exists(_mediaPath)) return;

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        _store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            SourceMediaPath = _mediaPath,
            IngestedMediaPath = _mediaPath,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _settings.TranscriptionProvider,
            TranscriptionModel = _settings.TranscriptionModel,
        });

        var changedSettings = CreateMatchingSettings();
        changedSettings.TranslationRuntime = InferenceRuntime.Local;
        changedSettings.TranslationProvider = ProviderNames.Nllb200;
        changedSettings.TranslationModel = "nllb-200-distilled-600M";

        var coord = CreateCoordinator(changedSettings);
        coord.Initialize();

        Assert.Equal(PipelineInvalidation.None, coord.CheckSettingsInvalidation());
    }

    [Fact]
    public void CheckSettingsInvalidation_Translated_TranslationModelChange_ReturnsTranslation()
    {
        if (!File.Exists(_mediaPath)) return;

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[]}");
        _store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Translated,
            SourceMediaPath = _mediaPath,
            IngestedMediaPath = _mediaPath,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranslationPath = translationPath,
            TargetLanguage = _settings.TargetLanguage,
            TranslatedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _settings.TranscriptionProvider,
            TranscriptionModel = _settings.TranscriptionModel,
            TranslationProvider = _settings.TranslationProvider,
            TranslationModel = _settings.TranslationModel,
        });

        var changedSettings = CreateMatchingSettings();
        changedSettings.TranslationRuntime = InferenceRuntime.Local;
        changedSettings.TranslationProvider = ProviderNames.Nllb200;
        changedSettings.TranslationModel = "nllb-200-distilled-600M";

        var coord = CreateCoordinator(changedSettings);
        coord.Initialize();

        Assert.Equal(PipelineInvalidation.Translation, coord.CheckSettingsInvalidation());
    }

    [Fact]
    public void CheckSettingsInvalidation_TtsGenerated_TtsVoiceChange_ReturnsTts()
    {
        if (!File.Exists(_mediaPath)) return;

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[]}");
        var ttsPath = CreateTempFile("tts");
        _store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            SourceMediaPath = _mediaPath,
            IngestedMediaPath = _mediaPath,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranslationPath = translationPath,
            TargetLanguage = _settings.TargetLanguage,
            TranslatedAtUtc = DateTimeOffset.UtcNow,
            TtsPath = ttsPath,
            TtsProvider = _settings.TtsProvider,
            TtsVoice = _settings.TtsVoice,
            TtsGeneratedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _settings.TranscriptionProvider,
            TranscriptionModel = _settings.TranscriptionModel,
            TranslationProvider = _settings.TranslationProvider,
            TranslationModel = _settings.TranslationModel,
        });

        var changedSettings = CreateMatchingSettings();
        changedSettings.TtsVoice = "en-US-GuyNeural";

        var coord = CreateCoordinator(changedSettings);
        coord.Initialize();

        Assert.Equal(PipelineInvalidation.Tts, coord.CheckSettingsInvalidation());
    }

    [Fact]
    public void CheckSettingsInvalidation_Transcribed_TtsProviderChanged_ReturnsNone()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"segments\":[]}");
        coord.InjectTestTranscript(transcriptPath, translationPath);

        // Set the session's TTS provider to match current settings
        _store.Save(coord.CurrentSession with
        {
            TranscriptionProvider = _settings.TranscriptionProvider,
            TranscriptionModel = _settings.TranscriptionModel,
            TranslationProvider = _settings.TranslationProvider,
            TranslationModel = _settings.TranslationModel,
            TtsProvider = _settings.TtsProvider,
            TtsVoice = _settings.TtsVoice,
            TargetLanguage = _settings.TargetLanguage,
        });

        // Now change TTS settings
        var changedSettings = CreateMatchingSettings();
        changedSettings.TtsRuntime = InferenceRuntime.Local;
        changedSettings.TtsProvider = ProviderNames.Piper;
        var coord2 = CreateCoordinator(changedSettings);
        coord2.Initialize();

        var invalidation = coord2.CheckSettingsInvalidation();
        Assert.Equal(PipelineInvalidation.None, invalidation);
    }

    [Fact]
    public void CheckSettingsInvalidation_TranscriptionProviderChanged_ReturnsTranscription()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        coord.InjectTestTranscript(transcriptPath);

        // Stamp matching settings
        _store.Save(coord.CurrentSession with
        {
            TranscriptionProvider = _settings.TranscriptionProvider,
            TranscriptionModel = _settings.TranscriptionModel,
            TranslationProvider = _settings.TranslationProvider,
            TranslationModel = _settings.TranslationModel,
            TtsProvider = _settings.TtsProvider,
            TtsVoice = _settings.TtsVoice,
            TargetLanguage = _settings.TargetLanguage,
        });

        // Change transcription provider
        var changedSettings = CreateMatchingSettings();
        changedSettings.TranscriptionRuntime = InferenceRuntime.Containerized;
        changedSettings.TranscriptionProvider = ProviderNames.FasterWhisper;
        var coord2 = CreateCoordinator(changedSettings);
        coord2.Initialize();

        var invalidation = coord2.CheckSettingsInvalidation();
        Assert.Equal(PipelineInvalidation.Transcription, invalidation);
    }

    [Fact]
    public void ApplyPipelineSettings_TranscriptionChange_ResetsPipelineAndRaisesSettingsModified()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"language_probability\":1.0,\"segments\":[{\"start\":0.0,\"end\":1.0,\"text\":\"hola\"}]}");
        var translationPath = CreateTempFile("{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[{\"id\":\"segment_0.0\",\"start\":0.0,\"end\":1.0,\"text\":\"hola\",\"translatedText\":\"hello\"}]}");
        _store.Save(coord.CurrentSession with
        {
            Stage = SessionWorkflowStage.Translated,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
            SourceLanguage = "es",
            TargetLanguage = _settings.TargetLanguage,
            TranscriptionProvider = _settings.TranscriptionProvider,
            TranscriptionModel = _settings.TranscriptionModel,
            TranslationProvider = _settings.TranslationProvider,
            TranslationModel = _settings.TranslationModel,
            TtsProvider = _settings.TtsProvider,
            TtsVoice = _settings.TtsVoice,
        });

        var coord2 = CreateCoordinator();
        coord2.Initialize();

        var settingsModified = false;
        coord2.SettingsModified += () => settingsModified = true;

        var result = coord2.ApplyPipelineSettings(new PipelineSettingsSelection(
            ComputeProfile.Gpu,
            ProviderNames.FasterWhisper,
            "base",
            _settings.TranslationProfile,
            _settings.TranslationProvider,
            _settings.TranslationModel,
            _settings.TtsProfile,
            _settings.TtsProvider,
            _settings.TtsVoice,
            _settings.TargetLanguage));

        Assert.True(settingsModified);
        Assert.Equal(PipelineInvalidation.Transcription, result.Invalidation);
        Assert.Equal(SessionWorkflowStage.MediaLoaded, coord2.CurrentSession.Stage);
        Assert.Null(coord2.CurrentSession.TranscriptPath);
        Assert.Null(coord2.CurrentSession.TranslationPath);
    }

    [Fact]
    public void ApplyPipelineSettings_ContainerizedRuntime_RequestsContainerStart()
    {
        var manager = new FakeContainerizedInferenceManager();
        var coord = CreateCoordinator(containerizedInferenceManager: manager);
        coord.Initialize();

        coord.ApplyPipelineSettings(new PipelineSettingsSelection(
            ComputeProfile.Gpu,
            ProviderNames.FasterWhisper,
            "base",
            _settings.TranslationProfile,
            _settings.TranslationProvider,
            _settings.TranslationModel,
            _settings.TtsProfile,
            _settings.TtsProvider,
            _settings.TtsVoice,
            _settings.TargetLanguage));

        Assert.Equal(1, manager.RequestCount);
        Assert.Equal(ContainerizedStartupTrigger.SettingsChanged, manager.LastRequestTrigger);
    }

    [Fact]
    public void RestoreSession_QueuesPausedMediaReloadRequest()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);
        var firstSessionId = coord.CurrentSession.SessionId;
        coord.ConsumePendingMediaReloadRequest();

        var secondMediaPath = Path.Combine(_dir, $"copy-{Guid.NewGuid():N}.mp4");
        File.Copy(_mediaPath, secondMediaPath);
        coord.LoadMedia(secondMediaPath);
        coord.ConsumePendingMediaReloadRequest();

        coord.RestoreSession(firstSessionId);

        var request = coord.ConsumePendingMediaReloadRequest();
        Assert.NotNull(request);
        Assert.False(request!.AutoPlay);
        Assert.Equal(coord.CurrentSession.IngestedMediaPath, request.IngestedMediaPath);
    }

    [Fact]
    public void SaveCurrentSession_MirrorsSnapshotToPerSessionStore()
    {
        if (!File.Exists(_mediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_mediaPath);
        coord.SaveCurrentSession();

        var mirrored = _perSessionStore.Load(coord.CurrentSession.SessionId);
        Assert.NotNull(mirrored);
        Assert.Equal(coord.CurrentSession.SessionId, mirrored!.SessionId);
        Assert.Equal(coord.CurrentSession.Stage, mirrored.Stage);
    }

    [Fact]
    public void Initialize_WithMissingTranslation_DowngradesAndClearsDownstreamProvenance()
    {
        if (!File.Exists(_mediaPath)) return;

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var missingTranslationPath = Path.Combine(_dir, "missing-translation.json");
        var existingTtsPath = CreateTempFile("tts");
        _store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            SourceMediaPath = _mediaPath,
            IngestedMediaPath = _mediaPath,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranslationPath = missingTranslationPath,
            TargetLanguage = _settings.TargetLanguage,
            TranslatedAtUtc = DateTimeOffset.UtcNow,
            TtsPath = existingTtsPath,
            TtsProvider = _settings.TtsProvider,
            TtsVoice = _settings.TtsVoice,
            TtsGeneratedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _settings.TranscriptionProvider,
            TranscriptionModel = _settings.TranscriptionModel,
            TranslationProvider = _settings.TranslationProvider,
            TranslationModel = _settings.TranslationModel,
        });

        var coord = CreateCoordinator();
        coord.Initialize();

        Assert.Equal(SessionWorkflowStage.Transcribed, coord.CurrentSession.Stage);
        Assert.Null(coord.CurrentSession.TranslationPath);
        Assert.Null(coord.CurrentSession.TranslationProvider);
        Assert.Null(coord.CurrentSession.TranslationModel);
        Assert.Null(coord.CurrentSession.TtsPath);
        Assert.Null(coord.CurrentSession.TtsProvider);
        Assert.Null(coord.CurrentSession.TtsVoice);
    }

    [Fact]
    public void ApplyPipelineSettings_AfterDowngradedStaleSession_ChangingTranslationModel_DoesNotResetBelowTranscribed()
    {
        if (!File.Exists(_mediaPath)) return;

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var missingTranslationPath = Path.Combine(_dir, "stale-translation.json");
        _store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Translated,
            SourceMediaPath = _mediaPath,
            IngestedMediaPath = _mediaPath,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranslationPath = missingTranslationPath,
            TargetLanguage = _settings.TargetLanguage,
            TranslatedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _settings.TranscriptionProvider,
            TranscriptionModel = _settings.TranscriptionModel,
            TranslationProvider = _settings.TranslationProvider,
            TranslationModel = _settings.TranslationModel,
        });

        var changedSettings = CreateMatchingSettings();
        changedSettings.TranslationRuntime = InferenceRuntime.Local;
        changedSettings.TranslationProvider = ProviderNames.Nllb200;
        changedSettings.TranslationModel = "nllb-200-distilled-600M";

        var coord = CreateCoordinator(changedSettings);
        coord.Initialize();

        var result = coord.ApplyPipelineSettings(new PipelineSettingsSelection(
            changedSettings.TranscriptionProfile,
            changedSettings.TranscriptionProvider,
            changedSettings.TranscriptionModel,
            changedSettings.TranslationProfile,
            changedSettings.TranslationProvider,
            changedSettings.TranslationModel,
            changedSettings.TtsProfile,
            changedSettings.TtsProvider,
            changedSettings.TtsVoice,
            changedSettings.TargetLanguage));

        Assert.Equal(SessionWorkflowStage.Transcribed, coord.CurrentSession.Stage);
        Assert.Equal(PipelineInvalidation.None, result.Invalidation);
        Assert.NotNull(coord.CurrentSession.TranscriptPath);
    }

    // ── SegmentId ─────────────────────────────────────────────────────────────

    [Fact]
    public void SegmentId_WholeNumber_FormatsWithOneDecimalPlace()
    {
        Assert.Equal("segment_0.0", SessionWorkflowCoordinator.SegmentId(0.0));
        Assert.Equal("segment_5.0", SessionWorkflowCoordinator.SegmentId(5.0));
        Assert.Equal("segment_100.0", SessionWorkflowCoordinator.SegmentId(100.0));
    }

    [Fact]
    public void SegmentId_Fractional_FormatsWithExactPrecision()
    {
        Assert.Equal("segment_3.68", SessionWorkflowCoordinator.SegmentId(3.68));
        Assert.Equal("segment_1.5", SessionWorkflowCoordinator.SegmentId(1.5));
    }

    [Fact]
    public void SegmentId_UsesInvariantCulture()
    {
        // Must use dot as decimal separator regardless of thread culture
        Assert.Contains(".", SessionWorkflowCoordinator.SegmentId(1.5));
        Assert.DoesNotContain(",", SessionWorkflowCoordinator.SegmentId(1.5));
    }

    // ── SaveCurrentSession ────────────────────────────────────────────────────

    [Fact]
    public void SaveCurrentSession_UpdatesLastUpdatedAtUtcAndPersistenceStatus()
    {
        var coord = CreateCoordinator();
        coord.Initialize();

        var before = coord.CurrentSession.LastUpdatedAtUtc;
        coord.SaveCurrentSession();

        Assert.False(string.IsNullOrWhiteSpace(coord.PersistenceStatus));
        Assert.True(coord.CurrentSession.LastUpdatedAtUtc >= before);
    }

    [Fact]
    public void SaveCurrentSession_PersistedSessionCanBeReloaded()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        coord.SaveCurrentSession();

        var coord2 = CreateCoordinator();
        coord2.Initialize();
        Assert.Equal(coord.CurrentSession.SessionId, coord2.CurrentSession.SessionId);
    }

    // ── StateFilePath / LogFilePath ───────────────────────────────────────────

    [Fact]
    public void StateFilePath_ReturnsStoreFilePath()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        Assert.Equal(_store.StateFilePath, coord.StateFilePath);
    }

    [Fact]
    public void LogFilePath_ReturnsLogFilePath()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        Assert.Equal(_log.LogFilePath, coord.LogFilePath);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string CreateTempFile(string content)
    {
        var path = Path.Combine(_dir, $"temp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class FakeTtsRegistry : ITtsRegistry
    {
        private readonly ITtsProvider _provider;

        public FakeTtsRegistry(ITtsProvider provider)
        {
            _provider = provider;
        }

        public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null) =>
        [
            new ProviderDescriptor(
                ProviderNames.EdgeTts,
                "Fake TTS",
                false,
                null,
                ["global-voice"],
                SupportedRuntimes: [InferenceRuntime.Cloud, InferenceRuntime.Containerized],
                DefaultRuntime: InferenceRuntime.Cloud)
        ];

        public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
            ["global-voice"];

        public ITtsProvider CreateProvider(
            string providerId,
            AppSettings settings,
            ApiKeyStore? keyStore = null,
            ComputeProfile? profile = null) => _provider;

        public ProviderReadiness CheckReadiness(
            string providerId,
            string modelOrVoice,
            AppSettings settings,
            ApiKeyStore? keyStore,
            ComputeProfile? profile = null) =>
            ProviderReadiness.Ready;

        public Task<bool> EnsureModelAsync(
            string providerId,
            string modelOrVoice,
            AppSettings settings,
            IProgress<double>? progress = null,
            CancellationToken ct = default,
            ComputeProfile? profile = null,
            ApiKeyStore? keyStore = null) =>
            Task.FromResult(true);
    }

    private sealed class FakeTtsProvider : ITtsProvider
    {
        public TtsRequest? LastRequest { get; private set; }
        public SingleSegmentTtsRequest? LastSegmentRequest { get; private set; }

        public Task<TtsResult> GenerateTtsAsync(TtsRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new TtsResult(true, request.OutputAudioPath, request.VoiceName, 1, null));
        }

        public Task<TtsResult> GenerateSegmentTtsAsync(SingleSegmentTtsRequest request, CancellationToken cancellationToken = default)
        {
            LastSegmentRequest = request;
            return Task.FromResult(new TtsResult(true, request.OutputAudioPath, request.VoiceName, 1, null));
        }
    }

    private sealed class FakeContainerizedInferenceManager : IContainerizedInferenceManager
    {
        public int RequestCount { get; private set; }
        public int EnsureCount { get; private set; }
        public ContainerizedStartupTrigger? LastRequestTrigger { get; private set; }

        public void RequestEnsureStarted(AppSettings settings, ContainerizedStartupTrigger trigger)
        {
            RequestCount++;
            LastRequestTrigger = trigger;
        }

        public Task<ContainerizedStartResult> EnsureStartedAsync(
            AppSettings settings,
            ContainerizedStartupTrigger trigger,
            CancellationToken cancellationToken = default)
        {
            EnsureCount++;
            return Task.FromResult(new ContainerizedStartResult(true, true, "started"));
        }
    }
}
