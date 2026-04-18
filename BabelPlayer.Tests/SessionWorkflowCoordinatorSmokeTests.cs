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
    public async Task Initialize_WithMissingTranscript_DowngradesToMediaLoaded()
    {
        var sessionDir = Path.Combine(_dir, "sessions", Guid.NewGuid().ToString("N"));
        var mediaDir = Path.Combine(sessionDir, "media");
        Directory.CreateDirectory(mediaDir);

        var sourcePath = CreateMediaFile("source-missing-transcript.mp4");
        var ingestedPath = Path.Combine(mediaDir, "source-missing-transcript.mp4");
        File.Copy(sourcePath, ingestedPath, overwrite: true);
        await ArtifactIntegrity.WriteFileManifestAsync(
                ingestedPath,
                "media_copy",
                artifactSchemaVersion: null,
                probedDurationSeconds: null,
                segmentCount: null,
                segmentIds: null,
                segmentTiming: null,
                upstreamArtifactHashes: null,
                provenanceDigest: ArtifactIntegrity.ComputeCompositeSha256(["stage=media_copy"]),
                CancellationToken.None);

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
    [Trait("Category", "Smoke")]
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
    [Trait("Category", "Smoke")]
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
    public void ResetPipelineToTranscribed_ClearsPerSegmentTimingState()
    {
        var coordinator = CreateCoordinator();
        coordinator.Initialize();

        var mediaPath = CreateMediaFile("transcribed-reset-source.mp4");
        var ingestedPath = CreateMediaFile("transcribed-reset-ingested.mp4");
        coordinator.CurrentSession = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Translated,
            SourceMediaPath = mediaPath,
            IngestedMediaPath = ingestedPath,
            SourceLanguage = "es",
            TargetLanguage = "en",
            TranscriptPath = "/session/transcript.json",
            TranslationPath = "/session/translation.json",
            TtsPath = "/session/tts.mp3",
            TtsSegmentsPath = "/session/tts/segments",
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
                ["segment_0.0"] = SegmentTimingMode.Stretch,
            },
            TranslationProvider = "fake-translation",
            TranslationModel = "default",
            TtsProvider = "fake-tts",
            TtsRuntime = InferenceRuntime.Cloud,
            TtsVoice = "default",
            SpeakerVoiceAssignments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["speaker_0"] = "default",
            },
            SpeakerReferenceAudioPaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["speaker_0"] = "/session/ref.wav",
            },
            DefaultTtsVoiceFallback = "fallback",
            DiarizationProvider = "fake-diarization",
            SpeakersDetectedAtUtc = DateTimeOffset.UtcNow,
        };

        coordinator.ResetPipelineToTranscribed();

        var session = coordinator.CurrentSession;
        Assert.Equal(SessionWorkflowStage.Transcribed, session.Stage);
        Assert.Null(session.TranslationPath);
        Assert.Null(session.TtsPath);
        Assert.Null(session.MixedDubAudioPath);
        Assert.Null(session.TtsSegmentsPath);
        Assert.Null(session.TtsSegmentAudioPaths);
        Assert.Null(session.TtsSegmentDurations);
        Assert.Null(session.SegmentTimingModeOverrides);
        Assert.Null(session.TranslationProvider);
        Assert.Null(session.TranslationModel);
        Assert.Null(session.TtsProvider);
        Assert.Null(session.TtsRuntime);
        Assert.Null(session.TtsVoice);
        Assert.Null(session.SpeakerVoiceAssignments);
        Assert.Null(session.SpeakerReferenceAudioPaths);
        Assert.Null(session.DefaultTtsVoiceFallback);
        Assert.Null(session.DiarizationProvider);
        Assert.Null(session.SpeakersDetectedAtUtc);
        Assert.Equal("Reset to transcription.", session.StatusMessage);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ResetPipelineToDiarized_ClearsPerSegmentTimingState()
    {
        var coordinator = CreateCoordinator();
        coordinator.Initialize();

        var mediaPath = CreateMediaFile("diarized-reset-source.mp4");
        var ingestedPath = CreateMediaFile("diarized-reset-ingested.mp4");
        coordinator.CurrentSession = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Diarized,
            SourceMediaPath = mediaPath,
            IngestedMediaPath = ingestedPath,
            SourceLanguage = "es",
            TargetLanguage = "en",
            TranslationPath = "/session/translation.json",
            TtsPath = "/session/tts.mp3",
            TtsSegmentsPath = "/session/tts/segments",
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
                ["segment_0.0"] = SegmentTimingMode.Stretch,
            },
            TtsProvider = "fake-tts",
            TtsRuntime = InferenceRuntime.Cloud,
            TtsVoice = "default",
            SpeakerVoiceAssignments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["speaker_0"] = "default",
            },
            SpeakerReferenceAudioPaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["speaker_0"] = "/session/ref.wav",
            },
            DiarizationProvider = "fake-diarization",
            SpeakersDetectedAtUtc = DateTimeOffset.UtcNow,
            TranslationProvider = "fake-translation",
            TranslationModel = "default",
            TranscriptionProvider = "fake-transcription",
            TranscriptionModel = "default",
        };

        coordinator.ResetPipelineToDiarized();

        var session = coordinator.CurrentSession;
        Assert.Equal(SessionWorkflowStage.Diarized, session.Stage);
        Assert.Null(session.TranslationPath);
        Assert.Null(session.TtsPath);
        Assert.Null(session.MixedDubAudioPath);
        Assert.Null(session.TtsSegmentsPath);
        Assert.Null(session.TtsSegmentAudioPaths);
        Assert.Null(session.TtsSegmentDurations);
        Assert.Null(session.TtsProvider);
        Assert.Null(session.TtsRuntime);
        Assert.Null(session.TtsVoice);
        Assert.Equal("Reset to speaker analysis.", session.StatusMessage);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ResetPipelineToTranslated_ClearsPerSegmentTimingState()
    {
        var coordinator = CreateCoordinator();
        coordinator.Initialize();

        var mediaPath = CreateMediaFile("translated-reset-source.mp4");
        var ingestedPath = CreateMediaFile("translated-reset-ingested.mp4");
        coordinator.CurrentSession = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            SourceMediaPath = mediaPath,
            IngestedMediaPath = ingestedPath,
            SourceLanguage = "es",
            TargetLanguage = "en",
            TtsPath = "/session/tts.mp3",
            MixedDubAudioPath = "/session/mixed.mp3",
            TtsSegmentsPath = "/session/tts/segments",
            TtsSegmentAudioPaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["segment_0.0"] = "/session/tts/segments/segment_0.0.mp3",
            },
            TtsSegmentDurations = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["segment_0.0"] = 1.5,
            },
            TtsProvider = "fake-tts",
            TtsRuntime = InferenceRuntime.Cloud,
            TtsVoice = "default",
            StatusMessage = "Before reset",
        };

        coordinator.ResetPipelineToTranslated();

        var session = coordinator.CurrentSession;
        Assert.Equal(SessionWorkflowStage.Translated, session.Stage);
        Assert.Null(session.TtsPath);
        Assert.Null(session.MixedDubAudioPath);
        Assert.Null(session.TtsSegmentsPath);
        Assert.Null(session.TtsSegmentAudioPaths);
        Assert.Null(session.TtsSegmentDurations);
        Assert.Null(session.SegmentTimingModeOverrides);
        Assert.Null(session.TtsProvider);
        Assert.Null(session.TtsRuntime);
        Assert.Null(session.TtsVoice);
        Assert.Equal("Reset to translation.", session.StatusMessage);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RegenerateSegmentTtsAsync_UpdatesSegmentAudioMap()
    {
        var coordinator = CreateCoordinator();
        coordinator.Initialize();

        coordinator.CurrentSession = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Translated,
            SourceLanguage = "es",
            TargetLanguage = "en",
            TranslationPath = WriteTranslationArtifact(
                new TranslationSegmentArtifact
                {
                    Id = "segment_0.0",
                    Start = 0.0,
                    End = 2.0,
                    Text = "hola",
                    TranslatedText = "hello",
                }),
            TtsSegmentAudioPaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["segment_0.0"] = "/session/preexisting/segment_0.0.mp3",
            },
        };

        await coordinator.RegenerateSegmentTtsAsync("segment_0.0");

        var session = coordinator.CurrentSession;
        Assert.NotNull(session.TtsSegmentAudioPaths);
        Assert.True(session.TtsSegmentAudioPaths.ContainsKey("segment_0.0"));
        Assert.True(File.Exists(session.TtsSegmentAudioPaths["segment_0.0"]));
        Assert.Contains("Regenerated TTS for segment segment_0.0.", session.StatusMessage);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RegenerateSegmentTranslationAsync_UpdatesTranslatedSegmentText()
    {
        _settings.TranslationProvider = ProviderNames.Deepl;
        _settings.TranslationModel = "default";
        _settings.TranslationProfile = ComputeProfile.Cloud;
        var coordinator = CreateCoordinator();
        coordinator.Initialize();

        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = _settings.TranslationProvider,
            TranslationModel = _settings.TranslationModel,
            TranslationRuntime = _settings.TranslationRuntime,
            SourceLanguage = "es",
            TargetLanguage = "en",
        };
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var translationPath = await SessionSemanticsIntegrityFixture.WriteTranslationAsync(_dir, transcriptPath, template);
        var artifact = await ArtifactJson.LoadTranslationAsync(translationPath);
        artifact.Segments![0].TranslatedText = "old";
        await SessionSemanticsIntegrityFixture.RewriteTranslationFileWithManifestAsync(
            translationPath,
            artifact,
            transcriptPath,
            template);

        coordinator.CurrentSession = template with
        {
            Stage = SessionWorkflowStage.Translated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
        };

        await coordinator.RegenerateSegmentTranslationAsync("segment_0.0");

        var refreshed = await ArtifactJson.LoadTranslationAsync(translationPath);
        var segment = Assert.Single(refreshed.Segments!);
        Assert.Equal("hola (en)", segment.TranslatedText);
        Assert.StartsWith(
            "Regenerated translation for segment segment_0.0.",
            coordinator.CurrentSession.StatusMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task GenerateTtsAsync_WithAmbiance_PersistsMixedDub()
    {
        var audioProcessing = new FakeAudioProcessingService();
        var coordinator = CreateCoordinator(audioProcessing);
        coordinator.Initialize();

        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            SourceLanguage = "es",
            TargetLanguage = "en",
            TtsProvider = "fake-tts",
            TtsVoice = "default",
        };
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var (vocalsPath, ambiancePath) = await SessionSemanticsIntegrityFixture.WriteStemPairAsync(_dir, mediaPath);
        var withStems = template with { VocalsAudioPath = vocalsPath, AmbianceAudioPath = ambiancePath };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, withStems);
        var translationPath = await SessionSemanticsIntegrityFixture.WriteTranslationAsync(_dir, transcriptPath, withStems);

        coordinator.CurrentSession = withStems with
        {
            Stage = SessionWorkflowStage.Translated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
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

        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            SourceLanguage = "es",
            TargetLanguage = "en",
            TtsProvider = "fake-tts",
            TtsVoice = "default",
        };
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var (vocalsPath, ambiancePath) = await SessionSemanticsIntegrityFixture.WriteStemPairAsync(_dir, mediaPath);
        var withStems = template with { VocalsAudioPath = vocalsPath, AmbianceAudioPath = ambiancePath };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, withStems);
        var translationPath = await SessionSemanticsIntegrityFixture.WriteTranslationAsync(_dir, transcriptPath, withStems);

        coordinator.CurrentSession = withStems with
        {
            Stage = SessionWorkflowStage.Translated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
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

    private string WriteTranslationArtifact(TranslationSegmentArtifact? firstSegment = null)
    {
        var path = Path.Combine(_dir, $"translation-{Guid.NewGuid():N}.json");
        var segment = firstSegment ?? new TranslationSegmentArtifact
        {
            Id = "segment_0.0",
            Start = 0.0,
            End = 2.0,
            Text = "hola",
            TranslatedText = "hello",
        };
        var artifact = new TranslationArtifact
        {
            SourceLanguage = "es",
            TargetLanguage = "en",
            Segments = [segment],
        };

        File.WriteAllText(path, ArtifactJson.SerializeTranslation(artifact));
        return path;
    }

    private static Task WriteMediaCopyManifestAsync(string mediaPath, CancellationToken cancellationToken = default) =>
        ArtifactIntegrity.WriteFileManifestAsync(
            mediaPath,
            "media_copy",
            artifactSchemaVersion: null,
            probedDurationSeconds: null,
            segmentCount: null,
            segmentIds: null,
            segmentTiming: null,
            upstreamArtifactHashes: null,
            provenanceDigest: ArtifactIntegrity.ComputeCompositeSha256(["stage=media_copy"]),
            cancellationToken);

    private async Task WriteTranscriptBundleAsync(
        string transcriptPath,
        string ingestedMediaPath,
        TranscriptArtifact artifact,
        string? vocalsPath = null,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(transcriptPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(transcriptPath, ArtifactJson.SerializeTranscript(artifact), cancellationToken)
            .ConfigureAwait(false);
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(
            ("media_copy", ingestedMediaPath),
            ("vocals_stem", vocalsPath));
        upstream.TryGetValue("media_copy", out var mediaHash);
        var provenance = ArtifactIntegrity.ComputeTranscriptionProvenanceDigest(
            mediaHash,
            vocalSeparationEnabled: !string.IsNullOrWhiteSpace(vocalsPath),
            _settings);
        await ArtifactIntegrity.WriteFileManifestAsync(
                transcriptPath,
                "transcript",
                artifact.SchemaVersion,
                probedDurationSeconds: null,
                segmentCount: artifact.Segments?.Count ?? 0,
                segmentIds: ArtifactIntegrity.BuildTranscriptSegmentIds(artifact.Segments),
                segmentTiming: ArtifactIntegrity.BuildTranscriptTimingSummary(artifact.Segments),
                upstreamArtifactHashes: upstream,
                provenanceDigest: provenance,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteStemPairManifestsAsync(
        string ingestedMediaPath,
        string vocalsPath,
        string ambiancePath,
        CancellationToken cancellationToken = default)
    {
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(("media_copy", ingestedMediaPath));
        upstream.TryGetValue("media_copy", out var mediaHash);
        var provenance = ArtifactIntegrity.ComputeCompositeSha256(
        [
            "stage=vocal_separation",
            $"media_copy={mediaHash ?? string.Empty}",
        ]);
        await ArtifactIntegrity.WriteFileManifestAsync(
                vocalsPath,
                "vocals_stem",
                artifactSchemaVersion: null,
                probedDurationSeconds: null,
                segmentCount: null,
                segmentIds: null,
                segmentTiming: null,
                upstreamArtifactHashes: upstream,
                provenanceDigest: provenance,
                cancellationToken)
            .ConfigureAwait(false);
        await ArtifactIntegrity.WriteFileManifestAsync(
                ambiancePath,
                "ambiance_stem",
                artifactSchemaVersion: null,
                probedDurationSeconds: null,
                segmentCount: null,
                segmentIds: null,
                segmentTiming: null,
                upstreamArtifactHashes: upstream,
                provenanceDigest: provenance,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteTranslationBundleAsync(
        string translationPath,
        string transcriptPath,
        TranslationArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(translationPath, ArtifactJson.SerializeTranslation(artifact), cancellationToken)
            .ConfigureAwait(false);
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(("transcript", transcriptPath));
        upstream.TryGetValue("transcript", out var transcriptHash);
        var provenance = ArtifactIntegrity.ComputeTranslationProvenanceDigest(
            transcriptHash,
            _settings,
            artifact.SourceLanguage ?? "es",
            artifact.TargetLanguage ?? "en");
        await ArtifactIntegrity.WriteFileManifestAsync(
                translationPath,
                "translation",
                artifact.SchemaVersion,
                probedDurationSeconds: null,
                segmentCount: artifact.Segments?.Count ?? 0,
                segmentIds: ArtifactIntegrity.BuildTranslationSegmentIds(artifact.Segments),
                segmentTiming: ArtifactIntegrity.BuildTranslationTimingSummary(artifact.Segments),
                upstreamArtifactHashes: upstream,
                provenanceDigest: provenance,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
