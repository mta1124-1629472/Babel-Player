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
