using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

public sealed class SessionWorkflowCoordinatorSmokeTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;
    private readonly SessionSnapshotStore _store;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;
    private readonly AppSettings _settings;

    public SessionWorkflowCoordinatorSmokeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-smoke-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "smoke.log"));
        _store = new SessionSnapshotStore(Path.Combine(_dir, "session.json"), _log);
        _perSessionStore = new PerSessionSnapshotStore(Path.Combine(_dir, "sessions"), _log);
        _recentStore = new RecentSessionsStore(Path.Combine(_dir, "recent-sessions.json"), _log);
        _settings = new AppSettings
        {
            TtsProvider = "fake-tts",
            TtsVoice = "default",
            TargetLanguage = "en",
        };
    }

    public void Dispose()
    {
        _log.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp files.
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Initialize_ThenLoadMedia_PersistsMediaLoadedSession()
    {
        var coordinator = CreateCoordinator();
        coordinator.Initialize();

        Assert.Equal(SessionWorkflowStage.Foundation, coordinator.CurrentSession.Stage);

        var mediaPath = CreateMediaFile();
        coordinator.LoadMedia(mediaPath);

        Assert.Equal(SessionWorkflowStage.MediaLoaded, coordinator.CurrentSession.Stage);
        Assert.Equal(mediaPath, coordinator.CurrentSession.SourceMediaPath);
        Assert.NotNull(coordinator.CurrentSession.IngestedMediaPath);
        Assert.True(File.Exists(coordinator.CurrentSession.IngestedMediaPath));

        var restored = CreateCoordinator();
        restored.Initialize();

        Assert.Equal(SessionWorkflowStage.MediaLoaded, restored.CurrentSession.Stage);
        Assert.Equal(mediaPath, restored.CurrentSession.SourceMediaPath);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Initialize_WithMissingTranscript_DowngradesToMediaLoaded()
    {
        var sessionDir = Path.Combine(_dir, "sessions", Guid.NewGuid().ToString("N"));
        var mediaDir = Path.Combine(sessionDir, "media");
        Directory.CreateDirectory(mediaDir);

        var sourcePath = CreateMediaFile("source-missing-transcript.mp4");
        var ingestedPath = Path.Combine(mediaDir, "source-missing-transcript.mp4");
        File.Copy(sourcePath, ingestedPath, overwrite: true);

        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            SourceMediaPath = sourcePath,
            IngestedMediaPath = ingestedPath,
            TranscriptPath = Path.Combine(sessionDir, "transcripts", "missing.json"),
            SourceLanguage = "es",
        };

        _store.Save(snapshot);

        var coordinator = CreateCoordinator();
        coordinator.Initialize();

        Assert.Equal(SessionWorkflowStage.MediaLoaded, coordinator.CurrentSession.Stage);
        Assert.Null(coordinator.CurrentSession.TranscriptPath);
    }

    [Fact]
    public void LoadMedia_NewSource_ReplacesStaleSessionState()
    {
        var coordinator = CreateCoordinator();
        coordinator.Initialize();

        var oldCreatedAt = DateTimeOffset.Parse("2024-01-15T12:00:00Z");
        var staleSourcePath = CreateMediaFile("stale-source.mp4");

        coordinator.CurrentSession = new WorkflowSessionSnapshot(
            Guid.NewGuid(),
            SessionWorkflowStage.TtsGenerated,
            oldCreatedAt,
            oldCreatedAt,
            "stale",
            SourceMediaPath: staleSourcePath,
            IngestedMediaPath: Path.Combine(_dir, "stale-ingested.mp4"),
            VocalsAudioPath: "/session/stems/vocals.wav",
            AmbianceAudioPath: "/session/stems/ambiance.wav",
            InstrumentalAudioPath: "/session/stems/instrumental.wav",
            MediaLoadedAtUtc: oldCreatedAt,
            TranscriptPath: "/session/transcripts/source.json",
            TranscribedAtUtc: oldCreatedAt,
            TranslationPath: "/session/translations/source.json",
            SourceLanguage: "es",
            TargetLanguage: "en",
            TranslatedAtUtc: oldCreatedAt,
            TtsPath: "/session/tts/source.mp3",
            MixedDubAudioPath: "/session/tts/source-mixed.mp3",
            TtsVoice: "custom-voice",
            TtsGeneratedAtUtc: oldCreatedAt,
            TtsSegmentsPath: "/session/tts/segments/source.json",
            TtsSegmentAudioPaths: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["segment_0.0"] = "/session/tts/segments/segment_0.0.mp3",
            },
            TtsSegmentDurations: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["segment_0.0"] = 1.25,
            },
            SpeakerVoiceAssignments: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["speaker_0"] = "speaker-voice",
            },
            SpeakerReferenceAudioPaths: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["speaker_0"] = "/session/tts/references/speaker_0.wav",
            },
            MultiSpeakerEnabled: false,
            DefaultTtsVoiceFallback: "fallback-voice",
            DiarizationProvider: "legacy-diarization",
            SpeakersDetectedAtUtc: oldCreatedAt,
            SegmentTimingModeOverrides: new Dictionary<string, SegmentTimingMode>(StringComparer.Ordinal)
            {
                ["segment_0.0"] = SegmentTimingMode.Stretch,
            });

        var mediaPath = CreateMediaFile("fresh-source.mp4");
        coordinator.LoadMedia(mediaPath);

        var session = coordinator.CurrentSession;
        Assert.Equal(SessionWorkflowStage.MediaLoaded, session.Stage);
        Assert.Equal(mediaPath, session.SourceMediaPath);
        Assert.NotNull(session.IngestedMediaPath);
        Assert.True(File.Exists(session.IngestedMediaPath));
        Assert.NotEqual(oldCreatedAt, session.CreatedAtUtc);
        Assert.Null(session.VocalsAudioPath);
        Assert.Null(session.AmbianceAudioPath);
        Assert.Null(session.InstrumentalAudioPath);
        Assert.Null(session.TranscriptPath);
        Assert.Null(session.TranslationPath);
        Assert.Null(session.TtsPath);
        Assert.Null(session.MixedDubAudioPath);
        Assert.Null(session.TtsSegmentsPath);
        Assert.Null(session.TtsSegmentAudioPaths);
        Assert.Null(session.TtsSegmentDurations);
        Assert.Null(session.SegmentTimingModeOverrides);
        Assert.Null(session.SpeakerVoiceAssignments);
        Assert.Null(session.SpeakerReferenceAudioPaths);
        Assert.Null(session.DefaultTtsVoiceFallback);
        Assert.Null(session.DiarizationProvider);
        Assert.Null(session.SpeakersDetectedAtUtc);
        Assert.True(session.MultiSpeakerEnabled);
    }

    [Fact]
    public void ResetPipelineToMediaLoaded_ClearsPerSegmentTimingState()
    {
        var coordinator = CreateCoordinator();
        coordinator.Initialize();

        var mediaPath = CreateMediaFile("reset-source.mp4");
        var ingestedPath = CreateMediaFile("reset-ingested.mp4");
        var now = DateTimeOffset.Parse("2025-01-15T12:00:00Z");

        coordinator.CurrentSession = WorkflowSessionSnapshot.CreateNew(now) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            SourceMediaPath = mediaPath,
            IngestedMediaPath = ingestedPath,
            MediaLoadedAtUtc = now,
            TtsSegmentsPath = "/session/tts/segments/source.json",
            TtsSegmentAudioPaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["segment_0.0"] = "/session/tts/segments/segment_0.0.mp3",
            },
            TtsSegmentDurations = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["segment_0.0"] = 1.5,
            },
            SegmentTimingModeOverrides = new Dictionary<string, SegmentTimingMode>(StringComparer.Ordinal)
            {
                ["segment_0.0"] = SegmentTimingMode.Pause,
            },
        };

        coordinator.ResetPipelineToMediaLoaded();

        var session = coordinator.CurrentSession;
        Assert.Equal(SessionWorkflowStage.MediaLoaded, session.Stage);
        Assert.Equal(mediaPath, session.SourceMediaPath);
        Assert.Equal(ingestedPath, session.IngestedMediaPath);
        Assert.Equal(now, session.MediaLoadedAtUtc);
        Assert.Null(session.TtsSegmentsPath);
        Assert.Null(session.TtsSegmentAudioPaths);
        Assert.Null(session.TtsSegmentDurations);
        Assert.Null(session.SegmentTimingModeOverrides);
        Assert.Equal("Ready.", session.StatusMessage);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task GenerateTtsAsync_WithAmbiance_PersistsMixedDub()
    {
        var audioProcessing = new FakeAudioProcessingService();
        var coordinator = CreateCoordinator(audioProcessing);
        coordinator.Initialize();

        coordinator.CurrentSession = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Translated,
            TranslationPath = WriteTranslationArtifact(),
            SourceLanguage = "es",
            TargetLanguage = "en",
            AmbianceAudioPath = WriteAudioFile("ambiance.wav"),
        };

        await coordinator.GenerateTtsAsync();

        Assert.True(audioProcessing.ComposeTimelineDubAsyncCalled);
        Assert.True(audioProcessing.MixDubOverAmbianceAsyncCalled);
        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.MixedDubAudioPath);
        Assert.True(File.Exists(coordinator.CurrentSession.MixedDubAudioPath));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task GenerateTtsAsync_WhenAmbianceMixOutputIsMissing_ThrowsAndLeavesSessionUnmixed()
    {
        var audioProcessing = new FakeAudioProcessingService
        {
            SkipMixedOutputCreation = true,
        };
        var coordinator = CreateCoordinator(audioProcessing);
        coordinator.Initialize();

        coordinator.CurrentSession = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Translated,
            TranslationPath = WriteTranslationArtifact(),
            SourceLanguage = "es",
            TargetLanguage = "en",
            AmbianceAudioPath = WriteAudioFile("ambiance-missing-output.wav"),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.GenerateTtsAsync());

        Assert.Contains("mix", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SessionWorkflowStage.Translated, coordinator.CurrentSession.Stage);
        Assert.Null(coordinator.CurrentSession.MixedDubAudioPath);
    }

    private SessionWorkflowCoordinator CreateCoordinator(IAudioProcessingService? audioProcessingService = null)
    {
        var registries = new RegistryBundle(
            _perSessionStore,
            _recentStore,
            new FakeTranscriptionRegistry(),
            new FakeTranslationRegistry(),
            new FakeTtsRegistry());

        return new SessionWorkflowCoordinator(
            new CoordinatorCoreServices(_store, _log, _settings),
            registries,
            new CoordinatorOptions
            {
                AudioProcessingService = audioProcessingService ?? new FakeAudioProcessingService(),
            });
    }

    private string CreateMediaFile(string fileName = "source.mp4")
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, "media");
        return path;
    }

    private string WriteAudioFile(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, [0x01, 0x02, 0x03]);
        return path;
    }

    private string WriteTranslationArtifact()
    {
        var path = Path.Combine(_dir, $"translation-{Guid.NewGuid():N}.json");
        var artifact = new TranslationArtifact
        {
            SourceLanguage = "es",
            TargetLanguage = "en",
            Segments =
            [
                new TranslationSegmentArtifact
                {
                    Id = "segment_0.0",
                    Start = 0.0,
                    End = 2.0,
                    Text = "hola",
                    TranslatedText = "hello",
                },
            ],
        };

        File.WriteAllText(path, ArtifactJson.SerializeTranslation(artifact));
        return path;
    }
}
