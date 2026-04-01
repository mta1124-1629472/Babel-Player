using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.ViewModels;

namespace BabelPlayer.Tests;

[Collection("Session workflow shared")]
public sealed class SessionWorkflowTests : IAsyncLifetime
{
    private readonly SessionWorkflowTemplateFixture _fixture;

    public SessionWorkflowTests(SessionWorkflowTemplateFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private SessionWorkflowCoordinator CreateCoordinator(
        SessionSnapshotStore store,
        AppLog log,
        string caseDir,
        IMediaTransport? segmentPlayer = null,
        IMediaTransport? sourcePlayer = null)
    {
        var settings = new Babel.Player.Services.Settings.AppSettings();
        var perSessionStore = new PerSessionSnapshotStore(Path.Combine(caseDir, "sessions"), log);
        var recentStore = new RecentSessionsStore(Path.Combine(caseDir, "recent-sessions.json"), log);
        
        var transcriptionRegistry = new Babel.Player.Services.Registries.TranscriptionRegistry(log);
        var translationRegistry = new Babel.Player.Services.Registries.TranslationRegistry(log);
        var ttsRegistry = new Babel.Player.Services.Registries.TtsRegistry(log);
        
        return new SessionWorkflowCoordinator(
            store, 
            log, 
            settings, 
            perSessionStore, 
            recentStore, 
            transcriptionRegistry,
            translationRegistry,
            ttsRegistry,
            segmentPlayer, 
            sourcePlayer);
    }

    private async Task<(SessionWorkflowCoordinator coordinator, SessionSnapshotStore store, AppLog log, string caseDir)>
        OpenCaseFromTemplateAsync(string templateName, string caseName)
    {
        var templateDir = await _fixture.GetPreparedTemplateAsync(templateName);
        var caseDir = _fixture.CreateCaseDirectory(caseName);

        SessionWorkflowTemplateFixture.CopyDirectory(templateDir, caseDir);

        var stateFilePath = SessionWorkflowTemplateFixture.GetStateFilePath(caseDir);
        var log = new AppLog(Path.Combine(caseDir, "case.log"));
        var store = new SessionSnapshotStore(stateFilePath, log);
        var coordinator = CreateCoordinator(store, log, caseDir);
        coordinator.Initialize();

        return (coordinator, store, log, caseDir);
    }

    private (SessionWorkflowCoordinator coordinator, SessionSnapshotStore store, AppLog log, string caseDir)
        CreateFreshCase(string caseName)
    {
        var caseDir = _fixture.CreateCaseDirectory(caseName);
        var stateFilePath = SessionWorkflowTemplateFixture.GetStateFilePath(caseDir);
        var log = new AppLog(Path.Combine(caseDir, "case.log"));
        var store = new SessionSnapshotStore(stateFilePath, log);
        var coordinator = CreateCoordinator(store, log, caseDir);
        coordinator.Initialize();

        return (coordinator, store, log, caseDir);
    }

    [Fact]
    public Task LoadMedia_ThenReopen_ReusesArtifact()
    {
        var (coordinator, store, log, caseDir) = CreateFreshCase(nameof(LoadMedia_ThenReopen_ReusesArtifact));

        coordinator.LoadMedia(_fixture.TestMediaPath);

        coordinator = CreateCoordinator(store, log, caseDir);
        coordinator.Initialize();

        Assert.Equal(SessionWorkflowStage.MediaLoaded, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.SourceMediaPath);
        Assert.Equal(_fixture.TestMediaPath, coordinator.CurrentSession.SourceMediaPath);
        Assert.DoesNotContain("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);

        return Task.CompletedTask;
    }

    [Fact]
    public Task ReopenWithMissingArtifact_SurfacesDegradedState()
    {
        var (coordinator, store, log, caseDir) = CreateFreshCase(nameof(ReopenWithMissingArtifact_SurfacesDegradedState));

        coordinator.LoadMedia(_fixture.TestMediaPath);

        var ingestedPath = coordinator.CurrentSession.IngestedMediaPath;
        Assert.NotNull(ingestedPath);

        if (File.Exists(ingestedPath))
        {
            File.Delete(ingestedPath);
        }

        coordinator = CreateCoordinator(store, log, caseDir);
        coordinator.Initialize();

        // Stage must be downgraded — ingested media is missing so media must be re-loaded
        Assert.Equal(SessionWorkflowStage.Foundation, coordinator.CurrentSession.Stage);
        Assert.Null(coordinator.CurrentSession.IngestedMediaPath);

        return Task.CompletedTask;
    }

    [Fact]
    [Trait("Category", "RequiresFfmpeg")]
    [Trait("Category", "RequiresPython")]
    public async Task TranscribeMedia_ThenReopen_ReusesTranscript()
    {
        var (coordinator, _, _, _) =
            await OpenCaseFromTemplateAsync("transcribed", nameof(TranscribeMedia_ThenReopen_ReusesTranscript));

        Assert.Equal(SessionWorkflowStage.Transcribed, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TranscriptPath);
        Assert.True(File.Exists(coordinator.CurrentSession.TranscriptPath));
        Assert.DoesNotContain("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "RequiresFfmpeg")]
    [Trait("Category", "RequiresPython")]
    public async Task ReopenWithMissingTranscript_SurfacesDegradedState()
    {
        var (coordinator, store, log, caseDir) =
            await OpenCaseFromTemplateAsync("transcribed", nameof(ReopenWithMissingTranscript_SurfacesDegradedState));

        Assert.NotNull(coordinator.CurrentSession.TranscriptPath);

        // Patch the snapshot to reference a non-existent file rather than deleting
        // the real AppData artifact — deleting it would corrupt the shared template.
        store.Save(coordinator.CurrentSession with { TranscriptPath = Path.Combine(caseDir, "nonexistent.json") });

        coordinator = CreateCoordinator(store, log, caseDir);
        coordinator.Initialize();

        // Stage must be downgraded — transcript is missing so transcription must re-run
        Assert.Equal(SessionWorkflowStage.MediaLoaded, coordinator.CurrentSession.Stage);
        Assert.Null(coordinator.CurrentSession.TranscriptPath);
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    [Trait("Category", "RequiresExternalTranslation")]
    public async Task TranslateTranscript_ThenReopen_ReusesTranslation()
    {
        var (coordinator, _, _, _) =
            await OpenCaseFromTemplateAsync("translated", nameof(TranslateTranscript_ThenReopen_ReusesTranslation));

        Assert.Equal(SessionWorkflowStage.Translated, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TranslationPath);
        Assert.True(File.Exists(coordinator.CurrentSession.TranslationPath));
        Assert.DoesNotContain("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("es", coordinator.CurrentSession.SourceLanguage);
        Assert.Equal("en", coordinator.CurrentSession.TargetLanguage);
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    [Trait("Category", "RequiresExternalTranslation")]
    public async Task ReopenWithMissingTranslation_SurfacesDegradedState()
    {
        var (coordinator, store, log, caseDir) =
            await OpenCaseFromTemplateAsync("translated", nameof(ReopenWithMissingTranslation_SurfacesDegradedState));

        Assert.NotNull(coordinator.CurrentSession.TranslationPath);

        // Patch the snapshot to reference a non-existent file rather than deleting
        // the real AppData artifact — deleting it would corrupt the shared template.
        store.Save(coordinator.CurrentSession with { TranslationPath = Path.Combine(caseDir, "nonexistent.json") });

        coordinator = CreateCoordinator(store, log, caseDir);
        coordinator.Initialize();

        // Stage must be downgraded — translation is missing so translation must re-run
        Assert.Equal(SessionWorkflowStage.Transcribed, coordinator.CurrentSession.Stage);
        Assert.Null(coordinator.CurrentSession.TranslationPath);
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    public async Task GenerateTts_ThenReopen_ReusesAudio()
    {
        var (coordinator, _, _, _) =
            await OpenCaseFromTemplateAsync("tts", nameof(GenerateTts_ThenReopen_ReusesAudio));

        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TtsPath);
        Assert.True(File.Exists(coordinator.CurrentSession.TtsPath));
        Assert.NotNull(coordinator.CurrentSession.TtsSegmentsPath);
        Assert.NotNull(coordinator.CurrentSession.TtsSegmentAudioPaths);
        Assert.DoesNotContain("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    public async Task ReopenWithMissingTts_SurfacesDegradedState()
    {
        var (coordinator, store, log, caseDir) =
            await OpenCaseFromTemplateAsync("tts", nameof(ReopenWithMissingTts_SurfacesDegradedState));

        var ttsPath = coordinator.CurrentSession.TtsPath;
        Assert.NotNull(ttsPath);

        File.Delete(ttsPath);

        coordinator = CreateCoordinator(store, log, caseDir);
        coordinator.Initialize();

        // Stage must be downgraded — TTS is missing so TTS must re-run
        Assert.Equal(SessionWorkflowStage.Translated, coordinator.CurrentSession.Stage);
        Assert.Null(coordinator.CurrentSession.TtsPath);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EndToEndPipeline_SmokeTest()
    {
        var caseDir = _fixture.CreateCaseDirectory(nameof(EndToEndPipeline_SmokeTest));
        var stateFilePath = SessionWorkflowTemplateFixture.GetStateFilePath(caseDir);
        var log = new AppLog(Path.Combine(caseDir, "case.log"));
        var store = new SessionSnapshotStore(stateFilePath, log);
        var coordinator = CreateCoordinator(store, log, caseDir);

        coordinator.Initialize();
        coordinator.LoadMedia(_fixture.TestMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync(targetLanguage: "en", sourceLanguage: "es");
        await coordinator.GenerateTtsAsync();

        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        Assert.True(File.Exists(coordinator.CurrentSession.TranscriptPath!));
        Assert.True(File.Exists(coordinator.CurrentSession.TranslationPath!));
        Assert.True(File.Exists(coordinator.CurrentSession.TtsPath!));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TranscribeMediaAsync_PersistsDetectedLanguage()
    {
        var (coordinator, _, _, _) =
            await OpenCaseFromTemplateAsync("transcribed", nameof(TranscribeMediaAsync_PersistsDetectedLanguage));

        Assert.NotNull(coordinator.CurrentSession.SourceLanguage);
        Assert.NotEmpty(coordinator.CurrentSession.SourceLanguage);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TranslateTranscriptAsync_NoSourceLangParam_UsesSessionLanguage()
    {
        // "transcribed" template has SourceLanguage set (after fix); translate without specifying source.
        var (coordinator, _, _, _) =
            await OpenCaseFromTemplateAsync("transcribed", nameof(TranslateTranscriptAsync_NoSourceLangParam_UsesSessionLanguage));

        var detectedLang = coordinator.CurrentSession.SourceLanguage;
        Assert.NotNull(detectedLang);

        await coordinator.TranslateTranscriptAsync();

        Assert.Equal(SessionWorkflowStage.Translated, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TranslationPath);
        Assert.True(File.Exists(coordinator.CurrentSession.TranslationPath));
        Assert.Equal(detectedLang, coordinator.CurrentSession.SourceLanguage);
        Assert.Equal("en", coordinator.CurrentSession.TargetLanguage);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RegenerateSegmentTranslation_ActuallyWritesNewTextToSegment()
    {
        var (coordinator, _, _, _) =
            await OpenCaseFromTemplateAsync("translated", nameof(RegenerateSegmentTranslation_ActuallyWritesNewTextToSegment));

        var translationPath = coordinator.CurrentSession.TranslationPath!;
        var jsonBefore = await File.ReadAllTextAsync(translationPath);
        var dataBefore = JsonSerializer.Deserialize<JsonElement>(jsonBefore);
        var firstSeg = dataBefore.GetProperty("segments")[0];
        var segmentId = firstSeg.GetProperty("id").GetString()!;

        const string sentinel = "CORRUPTED_SENTINEL_DO_NOT_PERSIST";
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            RewriteJsonWithCorruptedSegment(dataBefore, segmentId, sentinel, writer);
        }

        await File.WriteAllBytesAsync(translationPath, ms.ToArray());

        await coordinator.RegenerateSegmentTranslationAsync(segmentId);

        var jsonAfter = await File.ReadAllTextAsync(translationPath);
        var dataAfter = JsonSerializer.Deserialize<JsonElement>(jsonAfter);
        var segAfter = dataAfter.GetProperty("segments").EnumerateArray()
            .First(s => s.GetProperty("id").GetString() == segmentId);

        Assert.NotEqual(sentinel, segAfter.GetProperty("translatedText").GetString());
    }

    private static void RewriteJsonWithCorruptedSegment(
        JsonElement root,
        string targetId,
        string sentinel,
        Utf8JsonWriter writer)
    {
        writer.WriteStartObject();

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name != "segments")
            {
                prop.WriteTo(writer);
                continue;
            }

            writer.WritePropertyName("segments");
            writer.WriteStartArray();

            foreach (var seg in prop.Value.EnumerateArray())
            {
                var id = seg.GetProperty("id").GetString();

                if (id == targetId)
                {
                    writer.WriteStartObject();
                    foreach (var sp in seg.EnumerateObject())
                    {
                        if (sp.Name == "translatedText")
                        {
                            writer.WriteString("translatedText", sentinel);
                        }
                        else
                        {
                            sp.WriteTo(writer);
                        }
                    }

                    writer.WriteEndObject();
                }
                else
                {
                    seg.WriteTo(writer);
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}

internal sealed class FakeMediaTransport : IMediaTransport, IDisposable
{
    public string? LastLoadedFile { get; private set; }
    public long LastSeekPosition { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsDisposed { get; private set; }

    public long CurrentTime { get; set; }
    public long Duration { get; set; } = 10000;
    public bool HasEnded { get; set; }
    public double Volume { get; set; } = 1.0;
    public double PlaybackRate { get; set; } = 1.0;
    public bool SubtitlesVisible { get; set; }

    public event EventHandler? Ended;
    public event EventHandler<Exception>? ErrorOccurred;

    public void Load(string filePath) { LastLoadedFile = filePath; IsPlaying = false; IsPaused = true; HasEnded = false; }
    public void Play() { IsPlaying = true; IsPaused = false; }
    public void Pause() { IsPlaying = false; IsPaused = true; }
    public void Seek(long positionMs) { LastSeekPosition = positionMs; }
    public void LoadSubtitleTrack(string srtPath) { }
    public void RemoveAllSubtitleTracks() { }
    public void Dispose() { IsDisposed = true; }

    public void SimulateEnd() { HasEnded = true; Ended?.Invoke(this, EventArgs.Empty); }
    public void SimulateError(Exception ex) { ErrorOccurred?.Invoke(this, ex); }
}

[Collection("Session workflow shared")]
public sealed class EmbeddedPlaybackTests : IAsyncLifetime
{
    private readonly SessionWorkflowTemplateFixture _fixture;

    public EmbeddedPlaybackTests(SessionWorkflowTemplateFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private SessionWorkflowCoordinator CreateCoordinator(
        SessionSnapshotStore store,
        AppLog log,
        string caseDir,
        IMediaTransport? segmentPlayer = null,
        IMediaTransport? sourcePlayer = null)
    {
        var settings = new Babel.Player.Services.Settings.AppSettings();
        var perSessionStore = new PerSessionSnapshotStore(Path.Combine(caseDir, "sessions"), log);
        var recentStore = new RecentSessionsStore(Path.Combine(caseDir, "recent-sessions.json"), log);
        
        var transcriptionRegistry = new Babel.Player.Services.Registries.TranscriptionRegistry(log);
        var translationRegistry = new Babel.Player.Services.Registries.TranslationRegistry(log);
        var ttsRegistry = new Babel.Player.Services.Registries.TtsRegistry(log);
        
        return new SessionWorkflowCoordinator(
            store, 
            log, 
            settings, 
            perSessionStore, 
            recentStore, 
            transcriptionRegistry,
            translationRegistry,
            ttsRegistry,
            segmentPlayer, 
            sourcePlayer);
    }

    private async Task<(SessionWorkflowCoordinator coordinator, FakeMediaTransport sourcePlayer, string caseDir)>
        CreateCaseWithSourcePlayerAsync(string caseName)
    {
        var templateDir = await _fixture.GetPreparedTemplateAsync("tts");
        var caseDir = _fixture.CreateCaseDirectory(caseName);
        SessionWorkflowTemplateFixture.CopyDirectory(templateDir, caseDir);

        var stateFilePath = SessionWorkflowTemplateFixture.GetStateFilePath(caseDir);
        var log = new AppLog(Path.Combine(caseDir, "case.log"));
        var store = new SessionSnapshotStore(stateFilePath, log);

        var fakeSegmentPlayer = new FakeMediaTransport();
        var fakeSourcePlayer = new FakeMediaTransport();
        var coordinator = CreateCoordinator(store, log, caseDir, fakeSegmentPlayer, fakeSourcePlayer);
        coordinator.Initialize();

        return (coordinator, fakeSourcePlayer, caseDir);
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    public async Task PlaySourceMediaAtSegment_LoadsAndSeeks()
    {
        var (coordinator, sourcePlayer, _) = await CreateCaseWithSourcePlayerAsync(
            nameof(PlaySourceMediaAtSegment_LoadsAndSeeks));

        var segments = await coordinator.GetSegmentWorkflowListAsync();
        Assert.NotEmpty(segments);

        var first = segments[0];
        await coordinator.PlaySourceMediaAtSegmentAsync(first.SegmentId);

        Assert.NotNull(sourcePlayer.LastLoadedFile);
        Assert.True(sourcePlayer.IsPlaying);
        var expectedSeekMs = (long)(first.StartSeconds * 1000);
        Assert.Equal(expectedSeekMs, sourcePlayer.LastSeekPosition);
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    public async Task PlaySourceMediaAtSegment_InvalidSegment_Throws()
    {
        var (coordinator, _, _) = await CreateCaseWithSourcePlayerAsync(
            nameof(PlaySourceMediaAtSegment_InvalidSegment_Throws));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.PlaySourceMediaAtSegmentAsync("nonexistent-segment-id"));
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    public async Task StopSourceMedia_PausesPlayer()
    {
        var (coordinator, sourcePlayer, _) = await CreateCaseWithSourcePlayerAsync(
            nameof(StopSourceMedia_PausesPlayer));

        var segments = await coordinator.GetSegmentWorkflowListAsync();
        Assert.NotEmpty(segments);
        await coordinator.PlaySourceMediaAtSegmentAsync(segments[0].SegmentId);
        Assert.True(sourcePlayer.IsPlaying);

        coordinator.StopSourceMedia();
        Assert.True(sourcePlayer.IsPaused);
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    public async Task SourceMediaPlayer_Property_ReturnsInjectedPlayer()
    {
        var (coordinator, sourcePlayer, _) = await CreateCaseWithSourcePlayerAsync(
            nameof(SourceMediaPlayer_Property_ReturnsInjectedPlayer));

        // Force player creation by calling a method
        var segments = await coordinator.GetSegmentWorkflowListAsync();
        await coordinator.PlaySourceMediaAtSegmentAsync(segments[0].SegmentId);

        Assert.Same(sourcePlayer, coordinator.SourceMediaPlayer);
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    public async Task Dispose_CleansUpSourcePlayer()
    {
        var (coordinator, sourcePlayer, _) = await CreateCaseWithSourcePlayerAsync(
            nameof(Dispose_CleansUpSourcePlayer));

        // Force player creation
        var segments = await coordinator.GetSegmentWorkflowListAsync();
        await coordinator.PlaySourceMediaAtSegmentAsync(segments[0].SegmentId);

        coordinator.Dispose();

        // Injected player should NOT be disposed by coordinator (owned by caller)
        Assert.False(sourcePlayer.IsDisposed);
    }

    [Fact]
    public async Task PlaySourceMedia_NoSession_Throws()
    {
        var caseDir = _fixture.CreateCaseDirectory(nameof(PlaySourceMedia_NoSession_Throws));
        var stateFilePath = SessionWorkflowTemplateFixture.GetStateFilePath(caseDir);
        var log = new AppLog(Path.Combine(caseDir, "case.log"));
        var store = new SessionSnapshotStore(stateFilePath, log);
        var fakeSource = new FakeMediaTransport();
        var coordinator = CreateCoordinator(store, log, caseDir, sourcePlayer: fakeSource);
        coordinator.Initialize();

        // Fresh session has no media loaded
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.PlaySourceMediaAtSegmentAsync("any-id"));
    }
}

public sealed class SegmentInspectionTests
{
    private static SessionWorkflowCoordinator CreateCoordinator(
        SessionSnapshotStore store,
        AppLog log,
        string caseDir,
        IMediaTransport? segmentPlayer = null,
        IMediaTransport? sourcePlayer = null)
    {
        var settings = new Babel.Player.Services.Settings.AppSettings();
        var perSessionStore = new PerSessionSnapshotStore(Path.Combine(caseDir, "sessions"), log);
        var recentStore = new RecentSessionsStore(Path.Combine(caseDir, "recent-sessions.json"), log);
        
        var transcriptionRegistry = new Babel.Player.Services.Registries.TranscriptionRegistry(log);
        var translationRegistry = new Babel.Player.Services.Registries.TranslationRegistry(log);
        var ttsRegistry = new Babel.Player.Services.Registries.TtsRegistry(log);
        
        return new SessionWorkflowCoordinator(
            store, 
            log, 
            settings, 
            perSessionStore, 
            recentStore, 
            transcriptionRegistry,
            translationRegistry,
            ttsRegistry,
            segmentPlayer, 
            sourcePlayer);
    }

    private static EmbeddedPlaybackViewModel CreatePlaybackVm()
    {
        var caseDir = Path.Combine(Path.GetTempPath(), $"inspect-test-{Guid.NewGuid()}");
        var log = new AppLog(Path.Combine(Path.GetTempPath(), $"inspect-test-{Guid.NewGuid()}.log"));
        var storePath = Path.Combine(Path.GetTempPath(), $"inspect-test-{Guid.NewGuid()}.json");
        var store = new SessionSnapshotStore(storePath, log);
        var coordinator = CreateCoordinator(store, log, caseDir);
        coordinator.Initialize();
        return new EmbeddedPlaybackViewModel(coordinator);
    }

    [Fact]
    public void IsVisible_FalseWhenNoSegmentSelected()
    {
        var playback = CreatePlaybackVm();
        var inspection = new SegmentInspectionViewModel(playback);

        Assert.False(inspection.IsVisible);
        Assert.Equal("", inspection.SourceText);
        Assert.Equal("", inspection.TranslatedText);
        Assert.Equal("", inspection.TimingLabel);
        Assert.Equal("", inspection.SegmentId);
    }

    [Fact]
    public void Refresh_PopulatesAllProperties()
    {
        var playback = CreatePlaybackVm();
        var inspection = new SegmentInspectionViewModel(playback);

        var segment = new WorkflowSegmentState(
            "segment_1.5", 1.5, 4.0, "Hello world", true, "Hola mundo", true);

        inspection.Refresh(segment);

        Assert.True(inspection.IsVisible);
        Assert.Equal("segment_1.5", inspection.SegmentId);
        Assert.Equal("Hello world", inspection.SourceText);
        Assert.Equal("Hola mundo", inspection.TranslatedText);
        Assert.True(inspection.HasTranslation);
        Assert.True(inspection.HasTtsAudio);
        Assert.Equal("1.5s → 4.0s (2.5s)", inspection.TimingLabel);
    }

    [Fact]
    public void Refresh_ClearsWhenNull()
    {
        var playback = CreatePlaybackVm();
        var inspection = new SegmentInspectionViewModel(playback);

        var segment = new WorkflowSegmentState(
            "segment_0.0", 0.0, 2.0, "Some text", false, null, false);
        inspection.Refresh(segment);
        Assert.True(inspection.IsVisible);

        inspection.Refresh(null);
        Assert.False(inspection.IsVisible);
        Assert.Equal("", inspection.SourceText);
        Assert.Equal("", inspection.TranslatedText);
        Assert.Equal("", inspection.SegmentId);
    }

    [Fact]
    public void SelectedSegmentChange_UpdatesInspection()
    {
        var playback = CreatePlaybackVm();
        var inspection = new SegmentInspectionViewModel(playback);

        var segment = new WorkflowSegmentState(
            "segment_5.0", 5.0, 8.0, "Source line", true, "Translated line", false);

        playback.SelectedSegment = segment;

        Assert.True(inspection.IsVisible);
        Assert.Equal("Source line", inspection.SourceText);
        Assert.Equal("Translated line", inspection.TranslatedText);
        Assert.False(inspection.HasTtsAudio);

        playback.SelectedSegment = null;
        Assert.False(inspection.IsVisible);
    }
}
