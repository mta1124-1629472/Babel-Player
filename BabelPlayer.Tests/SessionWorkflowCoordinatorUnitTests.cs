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
public sealed class SessionWorkflowCoordinatorUnitTests() : IDisposable
{
    private readonly TestContext _ctx = new();

    private sealed class TestContext
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"babel-coord-unit-tests-{Guid.NewGuid():N}");
        public AppLog Log { get; }
        public SessionSnapshotStore Store { get; }
        public PerSessionSnapshotStore PerSessionStore { get; }
        public RecentSessionsStore RecentStore { get; }
        public AppSettings Settings { get; }
        public string MediaPath { get; }

        public TestContext()
        {
            Directory.CreateDirectory(Dir);
            Log = new AppLog(Path.Combine(Dir, "test.log"));
            Store = new SessionSnapshotStore(Path.Combine(Dir, "session.json"), Log);
            PerSessionStore = new PerSessionSnapshotStore(Path.Combine(Dir, "sessions"), Log);
            RecentStore = new RecentSessionsStore(Path.Combine(Dir, "recent-sessions.json"), Log);
            Settings = new AppSettings();
            MediaPath = Path.Combine(AppContext.BaseDirectory, "test-assets", "video", "sample.mp4");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_ctx.Dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private SessionWorkflowCoordinator CreateCoordinator(
        AppSettings? settings = null,
        IContainerizedInferenceManager? containerizedInferenceManager = null,
        IDiarizationRegistry? diarizationRegistry = null) =>
        new SessionWorkflowCoordinator(
            _ctx.Store,
            _ctx.Log,
            settings ?? _ctx.Settings,
            _ctx.PerSessionStore,
            _ctx.RecentStore,
            new TranscriptionRegistry(_ctx.Log),
            new TranslationRegistry(_ctx.Log),
            new TtsRegistry(_ctx.Log),
            containerizedInferenceManager: containerizedInferenceManager,
            diarizationRegistry: diarizationRegistry);

    private AppSettings CreateMatchingSettings() =>
        new()
        {
            TranscriptionRuntime = _ctx.Settings.TranscriptionRuntime,
            TranscriptionProvider = _ctx.Settings.TranscriptionProvider,
            TranscriptionModel = _ctx.Settings.TranscriptionModel,
            TranscriptionCpuComputeType = _ctx.Settings.TranscriptionCpuComputeType,
            TranscriptionCpuThreads = _ctx.Settings.TranscriptionCpuThreads,
            TranscriptionNumWorkers = _ctx.Settings.TranscriptionNumWorkers,
            TranslationRuntime = _ctx.Settings.TranslationRuntime,
            TranslationProvider = _ctx.Settings.TranslationProvider,
            TranslationModel = _ctx.Settings.TranslationModel,
            TtsRuntime = _ctx.Settings.TtsRuntime,
            TtsProvider = _ctx.Settings.TtsProvider,
            TtsVoice = _ctx.Settings.TtsVoice,
            TargetLanguage = _ctx.Settings.TargetLanguage,
            PiperModelDir = _ctx.Settings.PiperModelDir,
            ContainerizedServiceUrl = _ctx.Settings.ContainerizedServiceUrl,
            AlwaysRunContainerAtAppStart = _ctx.Settings.AlwaysRunContainerAtAppStart,
            VideoHwdec = _ctx.Settings.VideoHwdec,
            VideoGpuApi = _ctx.Settings.VideoGpuApi,
            VideoUseGpuNext = _ctx.Settings.VideoUseGpuNext,
            VideoVsrEnabled = _ctx.Settings.VideoVsrEnabled,
            VideoHdrEnabled = _ctx.Settings.VideoHdrEnabled,
            VideoToneMapping = _ctx.Settings.VideoToneMapping,
            VideoTargetPeak = _ctx.Settings.VideoTargetPeak,
            VideoHdrComputePeak = _ctx.Settings.VideoHdrComputePeak,
            VideoExportEncoder = _ctx.Settings.VideoExportEncoder,
            Theme = _ctx.Settings.Theme,
            MaxRecentSessions = _ctx.Settings.MaxRecentSessions,
            AutoSaveEnabled = _ctx.Settings.AutoSaveEnabled,
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
        _ctx.Store.Save(snapshot);

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
        _ctx.RecentStore.Upsert(entry);

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
        if (!File.Exists(_ctx.MediaPath)) return; // skip if media not present

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);
        Assert.Equal(SessionWorkflowStage.MediaLoaded, coord.CurrentSession.Stage);
    }

    [Fact]
    public void LoadMedia_ValidFile_SetsSourceMediaPath()
    {
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);
        Assert.Equal(_ctx.MediaPath, coord.CurrentSession.SourceMediaPath);
    }

    [Fact]
    public void LoadMedia_ValidFile_CopiesIngestedMedia()
    {
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);

        Assert.NotNull(coord.CurrentSession.IngestedMediaPath);
        Assert.True(File.Exists(coord.CurrentSession.IngestedMediaPath));
    }

    [Fact]
    public void LoadMedia_ValidFile_StatusMessageIndicatesReady()
    {
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);
        Assert.Contains("transcription", coord.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadMedia_ValidFile_QueuesNonAutoPlayMediaReloadRequest()
    {
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);

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
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);

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
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[]}");
        var ttsPath = CreateTempFile("fake audio");

        _ctx.Store.Save(coord.CurrentSession with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranslationPath = translationPath,
            TranslationProvider = _ctx.Settings.TranslationProvider,
            TranslationModel = _ctx.Settings.TranslationModel,
            TargetLanguage = _ctx.Settings.TargetLanguage,
            TranslatedAtUtc = DateTimeOffset.UtcNow,
            TtsPath = ttsPath,
            TtsProvider = _ctx.Settings.TtsProvider,
            TtsVoice = _ctx.Settings.TtsVoice,
            TtsGeneratedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _ctx.Settings.TranscriptionProvider,
            TranscriptionModel = _ctx.Settings.TranscriptionModel,
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
        if (!File.Exists(_ctx.MediaPath)) return;

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[]}");

        _ctx.Store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Translated,
            SourceMediaPath = _ctx.MediaPath,
            IngestedMediaPath = _ctx.MediaPath,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranslationPath = translationPath,
            TargetLanguage = _ctx.Settings.TargetLanguage,
            TranslatedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _ctx.Settings.TranscriptionProvider,
            TranscriptionModel = _ctx.Settings.TranscriptionModel,
            TranslationProvider = _ctx.Settings.TranslationProvider,
            TranslationModel = _ctx.Settings.TranslationModel,
            TtsProvider = _ctx.Settings.TtsProvider,
            TtsVoice = _ctx.Settings.TtsVoice,
        });

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.ResetPipelineToTranscribed();

        Assert.Equal(SessionWorkflowStage.Transcribed, coord.CurrentSession.Stage);
        Assert.Equal(_ctx.Settings.TranscriptionProvider, coord.CurrentSession.TranscriptionProvider);
        Assert.Equal(_ctx.Settings.TranscriptionModel, coord.CurrentSession.TranscriptionModel);
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
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);

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
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        coord.InjectTestTranscript(transcriptPath);
        coord.FlushPendingSave();

        // Verify the snapshot was written to disk with Transcribed stage
        var loaded = _ctx.Store.Load().Snapshot;
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
        coord.UpdateSettings(_ctx.Settings);
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
            _ctx.Store,
            _ctx.Log,
            _ctx.Settings,
            _ctx.PerSessionStore,
            _ctx.RecentStore,
            new TranscriptionRegistry(_ctx.Log),
            new TranslationRegistry(_ctx.Log),
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
            _ctx.Store,
            _ctx.Log,
            _ctx.Settings,
            _ctx.PerSessionStore,
            _ctx.RecentStore,
            new TranscriptionRegistry(_ctx.Log),
            new TranslationRegistry(_ctx.Log),
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
    public async Task RegenerateSegmentTts_SingleSpeakerQwen_UsesDefaultReferenceClip()
    {
        var fakeTts = new FakeTtsProvider();
        var fakeTtsRegistry = new FakeTtsRegistry(fakeTts);
        var settings = CreateMatchingSettings();
        settings.TtsProvider = ProviderNames.Qwen;

        var coord = new SessionWorkflowCoordinator(
            _ctx.Store,
            _ctx.Log,
            settings,
            _ctx.PerSessionStore,
            _ctx.RecentStore,
            new TranscriptionRegistry(_ctx.Log),
            new TranslationRegistry(_ctx.Log),
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
        var defaultRefPath = Path.Combine(_ctx.Dir, "qwen-single-ref.wav");
        await File.WriteAllBytesAsync(defaultRefPath, [1, 2, 3]);

        coord.CurrentSession = coord.CurrentSession with
        {
            Stage = SessionWorkflowStage.Translated,
            TranslationPath = translationPath,
            TtsVoice = "qwen-tts",
            MultiSpeakerEnabled = false,
            SpeakerReferenceAudioPaths = new Dictionary<string, string>
            {
                [QwenReferenceKeys.SingleSpeakerDefault] = defaultRefPath,
            },
        };

        await coord.RegenerateSegmentTtsAsync("segment_0.0");

        Assert.NotNull(fakeTts.LastSegmentRequest);
        Assert.Equal(defaultRefPath, fakeTts.LastSegmentRequest!.ReferenceAudioPath);
    }

    [Fact]
    public async Task RunDiarizationAsync_TranslatedSession_UpdatesTranscriptAndTranslationSpeakerIds()
    {
        var fakeProvider = new FakeDiarizationProvider(_ =>
            new DiarizationResult(
                true,
                [new DiarizedSegment(0.0, 1.0, "spk_01")],
                1,
                null));
        var fakeRegistry = new FakeDiarizationRegistry((ProviderNames.NemoLocal, "NeMo", fakeProvider));
        var settings = CreateMatchingSettings();
        settings.DiarizationProvider = ProviderNames.NemoLocal;

        var coord = CreateCoordinator(settings, diarizationRegistry: fakeRegistry);
        coord.Initialize();

        var transcriptPath = CreateTempFile("""{"language":"es","segments":[{"start":0.0,"end":1.0,"text":"hola"}]}""");
        var translationPath = CreateTempFile("""{"sourceLanguage":"es","targetLanguage":"en","segments":[{"id":"segment_0.0","start":0.0,"end":1.0,"text":"hola","translatedText":"hello"}]}""");
        var mediaPath = CreateTempFile("audio");

        coord.CurrentSession = coord.CurrentSession with
        {
            Stage = SessionWorkflowStage.Translated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
        };

        var speakerAssignmentsChanged = await coord.RunDiarizationAsync();

        var transcript = await ArtifactJson.LoadTranscriptAsync(transcriptPath);
        var translation = await ArtifactJson.LoadTranslationAsync(translationPath);

        Assert.True(speakerAssignmentsChanged);
        Assert.Equal("spk_01", transcript.Segments![0].SpeakerId);
        Assert.Equal("spk_01", translation.Segments![0].SpeakerId);
        Assert.Equal(SessionWorkflowStage.Translated, coord.CurrentSession.Stage);
        Assert.Equal(ProviderNames.NemoLocal, coord.CurrentSession.DiarizationProvider);
        Assert.NotNull(fakeProvider.LastRequest);
        Assert.Equal(mediaPath, fakeProvider.LastRequest!.SourceAudioPath);
    }

    [Fact]
    public async Task RunDiarizationAsync_WhenAssignmentsAreUnchanged_ReturnsFalse()
    {
        var fakeProvider = new FakeDiarizationProvider(_ =>
            new DiarizationResult(
                true,
                [new DiarizedSegment(0.0, 1.0, "spk_01")],
                1,
                null));
        var fakeRegistry = new FakeDiarizationRegistry((ProviderNames.NemoLocal, "NeMo", fakeProvider));
        var settings = CreateMatchingSettings();
        settings.DiarizationProvider = ProviderNames.NemoLocal;

        var coord = CreateCoordinator(settings, diarizationRegistry: fakeRegistry);
        coord.Initialize();

        var transcriptPath = CreateTempFile("""{"language":"es","segments":[{"start":0.0,"end":1.0,"text":"hola","speakerId":"spk_01"}]}""");
        var translationPath = CreateTempFile("""{"sourceLanguage":"es","targetLanguage":"en","segments":[{"id":"segment_0.0","start":0.0,"end":1.0,"text":"hola","translatedText":"hello","speakerId":"spk_01"}]}""");
        var mediaPath = CreateTempFile("audio");

        coord.CurrentSession = coord.CurrentSession with
        {
            Stage = SessionWorkflowStage.Translated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
        };

        var speakerAssignmentsChanged = await coord.RunDiarizationAsync();

        Assert.False(speakerAssignmentsChanged);
    }

    [Fact]
    public async Task RunDiarizationAsync_WhenProviderReturnsFailure_Throws()
    {
        var fakeProvider = new FakeDiarizationProvider(_ =>
            new DiarizationResult(
                false,
                [],
                0,
                "diarization failed"));
        var fakeRegistry = new FakeDiarizationRegistry((ProviderNames.NemoLocal, "NeMo", fakeProvider));
        var settings = CreateMatchingSettings();
        settings.DiarizationProvider = ProviderNames.NemoLocal;

        var coord = CreateCoordinator(settings, diarizationRegistry: fakeRegistry);
        coord.Initialize();

        var transcriptPath = CreateTempFile("""{"language":"es","segments":[{"start":0.0,"end":1.0,"text":"hola"}]}""");
        var mediaPath = CreateTempFile("audio");

        coord.CurrentSession = coord.CurrentSession with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => coord.RunDiarizationAsync());

        Assert.Contains("diarization failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunDiarizationAsync_WhenRegistryIsMissing_Throws()
    {
        var settings = CreateMatchingSettings();
        settings.DiarizationProvider = ProviderNames.NemoLocal;

        var coord = CreateCoordinator(settings, diarizationRegistry: null);
        coord.Initialize();

        var transcriptPath = CreateTempFile("""{"language":"es","segments":[{"start":0.0,"end":1.0,"text":"hola"}]}""");
        var mediaPath = CreateTempFile("audio");

        coord.CurrentSession = coord.CurrentSession with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
        };

        var ex = await Assert.ThrowsAsync<PipelineProviderException>(() => coord.RunDiarizationAsync());

        Assert.Contains("No diarization registry is configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunDiarizationAsync_WhenProviderIsNotReady_Throws()
    {
        var fakeProvider = new FakeDiarizationProvider(
            readiness: new ProviderReadiness(false, "host is warming"));
        var fakeRegistry = new FakeDiarizationRegistry((ProviderNames.NemoLocal, "NeMo", fakeProvider));
        var settings = CreateMatchingSettings();
        settings.DiarizationProvider = ProviderNames.NemoLocal;

        var coord = CreateCoordinator(settings, diarizationRegistry: fakeRegistry);
        coord.Initialize();

        var transcriptPath = CreateTempFile("""{"language":"es","segments":[{"start":0.0,"end":1.0,"text":"hola"}]}""");
        var mediaPath = CreateTempFile("audio");

        coord.CurrentSession = coord.CurrentSession with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
        };

        var ex = await Assert.ThrowsAsync<PipelineProviderException>(() => coord.RunDiarizationAsync());

        Assert.Contains("host is warming", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── CheckSettingsInvalidation ─────────────────────────────────────────────

    [Fact]
    public void CheckSettingsInvalidation_NothingChanged_ReturnsNone()
    {
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"segments\":[]}");
        coord.InjectTestTranscript(transcriptPath, translationPath);

        // Stamp the current provider settings into the snapshot
        var session = coord.CurrentSession with
        {
            TranscriptionProvider = _ctx.Settings.TranscriptionProvider,
            TranscriptionModel = _ctx.Settings.TranscriptionModel,
            TranslationProvider = _ctx.Settings.TranslationProvider,
            TranslationModel = _ctx.Settings.TranslationModel,
            TtsProvider = _ctx.Settings.TtsProvider,
            TtsVoice = _ctx.Settings.TtsVoice,
            TargetLanguage = _ctx.Settings.TargetLanguage,
        };
        // Directly simulate the coordinator state as if the pipeline had run with current settings
        // by updating the store and reinitialising
        _ctx.Store.Save(session);
        var coord2 = CreateCoordinator();
        coord2.Initialize();

        var invalidation = coord2.CheckSettingsInvalidation();
        Assert.Equal(PipelineInvalidation.None, invalidation);
    }

    [Fact]
    public void CheckSettingsInvalidation_MediaLoaded_TranslationModelChange_ReturnsNone()
    {
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);

        var changedSettings = CreateMatchingSettings();
        changedSettings.TranslationModel = "nllb-200-1.3B";

        var coord2 = CreateCoordinator(changedSettings);
        coord2.Initialize();
        coord2.LoadMedia(_ctx.MediaPath);

        Assert.Equal(PipelineInvalidation.None, coord2.CheckSettingsInvalidation());
    }

    [Fact]
    public void CheckSettingsInvalidation_Transcribed_TranslationModelChange_ReturnsNone()
    {
        if (!File.Exists(_ctx.MediaPath)) return;

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        _ctx.Store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            SourceMediaPath = _ctx.MediaPath,
            IngestedMediaPath = _ctx.MediaPath,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _ctx.Settings.TranscriptionProvider,
            TranscriptionModel = _ctx.Settings.TranscriptionModel,
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
        if (!File.Exists(_ctx.MediaPath)) return;

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[]}");
        _ctx.Store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Translated,
            SourceMediaPath = _ctx.MediaPath,
            IngestedMediaPath = _ctx.MediaPath,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranslationPath = translationPath,
            TargetLanguage = _ctx.Settings.TargetLanguage,
            TranslatedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _ctx.Settings.TranscriptionProvider,
            TranscriptionModel = _ctx.Settings.TranscriptionModel,
            TranslationProvider = _ctx.Settings.TranslationProvider,
            TranslationModel = _ctx.Settings.TranslationModel,
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
        if (!File.Exists(_ctx.MediaPath)) return;

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[]}");
        var ttsPath = CreateTempFile("tts");
        _ctx.Store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            SourceMediaPath = _ctx.MediaPath,
            IngestedMediaPath = _ctx.MediaPath,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranslationPath = translationPath,
            TargetLanguage = _ctx.Settings.TargetLanguage,
            TranslatedAtUtc = DateTimeOffset.UtcNow,
            TtsPath = ttsPath,
            TtsProvider = _ctx.Settings.TtsProvider,
            TtsVoice = _ctx.Settings.TtsVoice,
            TtsGeneratedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _ctx.Settings.TranscriptionProvider,
            TranscriptionModel = _ctx.Settings.TranscriptionModel,
            TranslationProvider = _ctx.Settings.TranslationProvider,
            TranslationModel = _ctx.Settings.TranslationModel,
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
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var translationPath = CreateTempFile("{\"segments\":[]}");
        coord.InjectTestTranscript(transcriptPath, translationPath);

        // Set the session's TTS provider to match current settings
        _ctx.Store.Save(coord.CurrentSession with
        {
            TranscriptionProvider = _ctx.Settings.TranscriptionProvider,
            TranscriptionModel = _ctx.Settings.TranscriptionModel,
            TranslationProvider = _ctx.Settings.TranslationProvider,
            TranslationModel = _ctx.Settings.TranslationModel,
            TtsProvider = _ctx.Settings.TtsProvider,
            TtsVoice = _ctx.Settings.TtsVoice,
            TargetLanguage = _ctx.Settings.TargetLanguage,
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
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        coord.InjectTestTranscript(transcriptPath);

        // Stamp matching settings
        _ctx.Store.Save(coord.CurrentSession with
        {
            TranscriptionProvider = _ctx.Settings.TranscriptionProvider,
            TranscriptionModel = _ctx.Settings.TranscriptionModel,
            TranslationProvider = _ctx.Settings.TranslationProvider,
            TranslationModel = _ctx.Settings.TranslationModel,
            TtsProvider = _ctx.Settings.TtsProvider,
            TtsVoice = _ctx.Settings.TtsVoice,
            TargetLanguage = _ctx.Settings.TargetLanguage,
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
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"language_probability\":1.0,\"segments\":[{\"start\":0.0,\"end\":1.0,\"text\":\"hola\"}]}");
        var translationPath = CreateTempFile("{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[{\"id\":\"segment_0.0\",\"start\":0.0,\"end\":1.0,\"text\":\"hola\",\"translatedText\":\"hello\"}]}");
        _ctx.Store.Save(coord.CurrentSession with
        {
            Stage = SessionWorkflowStage.Translated,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
            SourceLanguage = "es",
            TargetLanguage = _ctx.Settings.TargetLanguage,
            TranscriptionProvider = _ctx.Settings.TranscriptionProvider,
            TranscriptionModel = _ctx.Settings.TranscriptionModel,
            TranslationProvider = _ctx.Settings.TranslationProvider,
            TranslationModel = _ctx.Settings.TranslationModel,
            TtsProvider = _ctx.Settings.TtsProvider,
            TtsVoice = _ctx.Settings.TtsVoice,
        });

        var coord2 = CreateCoordinator();
        coord2.Initialize();

        var settingsModified = false;
        coord2.SettingsModified += () => settingsModified = true;

        var result = coord2.ApplyPipelineSettings(new PipelineSettingsSelection(
            ComputeProfile.Gpu,
            ProviderNames.FasterWhisper,
            "base",
            _ctx.Settings.TranslationProfile,
            _ctx.Settings.TranslationProvider,
            _ctx.Settings.TranslationModel,
            _ctx.Settings.TtsProfile,
            _ctx.Settings.TtsProvider,
            _ctx.Settings.TtsVoice,
            _ctx.Settings.TargetLanguage));

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
            _ctx.Settings.TranslationProfile,
            _ctx.Settings.TranslationProvider,
            _ctx.Settings.TranslationModel,
            _ctx.Settings.TtsProfile,
            _ctx.Settings.TtsProvider,
            _ctx.Settings.TtsVoice,
            _ctx.Settings.TargetLanguage));

        Assert.Equal(1, manager.RequestCount);
        Assert.Equal(ContainerizedStartupTrigger.SettingsChanged, manager.LastRequestTrigger);
    }

    [Fact]
    public void RestoreSession_QueuesPausedMediaReloadRequest()
    {
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);
        var firstSessionId = coord.CurrentSession.SessionId;
        coord.ConsumePendingMediaReloadRequest();

        var secondMediaPath = Path.Combine(_ctx.Dir, $"copy-{Guid.NewGuid():N}.mp4");
        File.Copy(_ctx.MediaPath, secondMediaPath);
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
        if (!File.Exists(_ctx.MediaPath)) return;

        var coord = CreateCoordinator();
        coord.Initialize();
        coord.LoadMedia(_ctx.MediaPath);
        coord.SaveCurrentSession();

        var mirrored = _ctx.PerSessionStore.Load(coord.CurrentSession.SessionId);
        Assert.NotNull(mirrored);
        Assert.Equal(coord.CurrentSession.SessionId, mirrored!.SessionId);
        Assert.Equal(coord.CurrentSession.Stage, mirrored.Stage);
    }

    [Fact]
    public void Initialize_WithMissingTranslation_DowngradesAndClearsDownstreamProvenance()
    {
        if (!File.Exists(_ctx.MediaPath)) return;

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var missingTranslationPath = Path.Combine(_ctx.Dir, "missing-translation.json");
        var existingTtsPath = CreateTempFile("tts");
        _ctx.Store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            SourceMediaPath = _ctx.MediaPath,
            IngestedMediaPath = _ctx.MediaPath,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranslationPath = missingTranslationPath,
            TargetLanguage = _ctx.Settings.TargetLanguage,
            TranslatedAtUtc = DateTimeOffset.UtcNow,
            TtsPath = existingTtsPath,
            TtsProvider = _ctx.Settings.TtsProvider,
            TtsVoice = _ctx.Settings.TtsVoice,
            TtsGeneratedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _ctx.Settings.TranscriptionProvider,
            TranscriptionModel = _ctx.Settings.TranscriptionModel,
            TranslationProvider = _ctx.Settings.TranslationProvider,
            TranslationModel = _ctx.Settings.TranslationModel,
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
        if (!File.Exists(_ctx.MediaPath)) return;

        var transcriptPath = CreateTempFile("{\"language\":\"es\",\"segments\":[]}");
        var missingTranslationPath = Path.Combine(_ctx.Dir, "stale-translation.json");
        _ctx.Store.Save(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Translated,
            SourceMediaPath = _ctx.MediaPath,
            IngestedMediaPath = _ctx.MediaPath,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
            TranscribedAtUtc = DateTimeOffset.UtcNow,
            TranslationPath = missingTranslationPath,
            TargetLanguage = _ctx.Settings.TargetLanguage,
            TranslatedAtUtc = DateTimeOffset.UtcNow,
            TranscriptionProvider = _ctx.Settings.TranscriptionProvider,
            TranscriptionModel = _ctx.Settings.TranscriptionModel,
            TranslationProvider = _ctx.Settings.TranslationProvider,
            TranslationModel = _ctx.Settings.TranslationModel,
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
        coord.FlushPendingSave();

        var coord2 = CreateCoordinator();
        coord2.Initialize();
        Assert.Equal(coord.CurrentSession.SessionId, coord2.CurrentSession.SessionId);
    }

    // ── FlushPendingSave ──────────────────────────────────────────────────────

    [Fact]
    public void FlushPendingSave_UpdatesLastUpdatedAtUtcAndPersistenceStatus()
    {
        var coord = CreateCoordinator();
        coord.Initialize();

        var before = coord.CurrentSession.LastUpdatedAtUtc;
        coord.FlushPendingSave();

        Assert.False(string.IsNullOrWhiteSpace(coord.PersistenceStatus));
        Assert.True(coord.CurrentSession.LastUpdatedAtUtc >= before);
    }

    [Fact]
    public void FlushPendingSave_PersistedSnapshotMatchesCurrentSession()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        coord.FlushPendingSave();

        // Reloading in a new coordinator should restore the same session
        var coord2 = CreateCoordinator();
        coord2.Initialize();
        Assert.Equal(coord.CurrentSession.SessionId, coord2.CurrentSession.SessionId);
    }

    [Fact]
    public void FlushPendingSave_CalledMultipleTimes_DoesNotThrow()
    {
        var coord = CreateCoordinator();
        coord.Initialize();

        var ex = Record.Exception(() =>
        {
            coord.FlushPendingSave();
            coord.FlushPendingSave();
            coord.FlushPendingSave();
        });

        Assert.Null(ex);
    }

    // ── RegenerateSegmentTranslationAsync ────────────────────────────────────

    [Fact]
    public async Task RegenerateSegmentTranslationAsync_NoTranslationPath_ThrowsInvalidOperationException()
    {
        var coord = CreateCoordinator();
        coord.Initialize();

        // TranslationPath is null by default in a Foundation session
        Assert.Null(coord.CurrentSession.TranslationPath);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coord.RegenerateSegmentTranslationAsync("segment_0.0"));

        Assert.Contains("translate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegenerateSegmentTranslationAsync_TranslationFileNotFound_ThrowsFileNotFoundException()
    {
        var coord = CreateCoordinator();
        coord.Initialize();

        // Set TranslationPath to a non-existent file
        coord.CurrentSession = coord.CurrentSession with
        {
            TranslationPath = Path.Combine(_ctx.Dir, "missing-translation.json")
        };

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => coord.RegenerateSegmentTranslationAsync("segment_0.0"));
    }

    // ── StateFilePath / LogFilePath ───────────────────────────────────────────

    [Fact]
    public void StateFilePath_ReturnsStoreFilePath()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        Assert.Equal(_ctx.Store.StateFilePath, coord.StateFilePath);
    }

    [Fact]
    public void LogFilePath_ReturnsLogFilePath()
    {
        var coord = CreateCoordinator();
        coord.Initialize();
        Assert.Equal(_ctx.Log.LogFilePath, coord.LogFilePath);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string CreateTempFile(string content)
    {
        var path = Path.Combine(_ctx.Dir, $"temp-{Guid.NewGuid():N}.json");
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
