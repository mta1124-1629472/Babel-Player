using System;
using System.Collections.Generic;
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
public sealed class SessionWorkflowTests(SessionWorkflowTemplateFixture fixture) : IAsyncLifetime
{
    private readonly SessionWorkflowTemplateFixture _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    public record TestContext(
        SessionWorkflowCoordinator Coordinator,
        SessionSnapshotStore Store,
        AppLog Log,
        string CaseDir,
        FakeMediaTransport? SourcePlayer = null,
        FakeMediaTransport? SegmentPlayer = null);

    private static SessionWorkflowCoordinator CreateCoordinator(
        SessionSnapshotStore store,
        AppLog log,
        string caseDir,
        IMediaTransport? segmentPlayer = null,
        IMediaTransport? sourcePlayer = null,
        Babel.Player.Services.Settings.AppSettings? settings = null,
        Babel.Player.Services.Registries.IDiarizationRegistry? diarizationRegistry = null)
    {
        settings ??= new Babel.Player.Services.Settings.AppSettings();
        var perSessionStore = new PerSessionSnapshotStore(Path.Combine(caseDir, "sessions"), log);
        var recentStore = new RecentSessionsStore(Path.Combine(caseDir, "recent-sessions.json"), log);

        var transcriptionRegistry = new Babel.Player.Services.Registries.TranscriptionRegistry(log);
        var translationRegistry = new Babel.Player.Services.Registries.TranslationRegistry(log);
        var ttsRegistry = new Babel.Player.Services.Registries.TtsRegistry(log);
        diarizationRegistry ??= new FakeDiarizationRegistry(
            (ProviderNames.NemoLocal, "NeMo", new FakeDiarizationProvider()),
            (ProviderNames.WeSpeakerLocal, "WeSpeaker", new FakeDiarizationProvider()));
        var audioProcessingService = new FfmpegAudioProcessingService(log);

        return new SessionWorkflowCoordinator(
            store,
            log,
            settings,
            perSessionStore,
            recentStore,
            transcriptionRegistry,
            translationRegistry,
            ttsRegistry,
            diarizationRegistry: diarizationRegistry,
            segmentPlayer: segmentPlayer,
            sourcePlayer: sourcePlayer,
            audioProcessingService: audioProcessingService);
    }

    private static SessionWorkflowCoordinator CreateCoordinator(
        SessionSnapshotStore store,
        AppLog log,
        string caseDir,
        Babel.Player.Services.Settings.AppSettings settings,
        Babel.Player.Services.Registries.IDiarizationRegistry diarizationRegistry) =>
        CreateCoordinator(store, log, caseDir, null, null, settings, diarizationRegistry);

    private async Task<TestContext> OpenCaseFromTemplateAsync(string templateName, string caseName)
    {
        var templateDir = await _fixture.GetPreparedTemplateAsync(templateName);
        var caseDir = _fixture.CreateCaseDirectory(caseName);

        SessionWorkflowTemplateFixture.CopyDirectory(templateDir, caseDir);

        var stateFilePath = SessionWorkflowTemplateFixture.GetStateFilePath(caseDir);
        var log = new AppLog(Path.Combine(caseDir, "case.log"));
        var store = new SessionSnapshotStore(stateFilePath, log);
        var coordinator = CreateCoordinator(store, log, caseDir);
        coordinator.Initialize();

        return new TestContext(coordinator, store, log, caseDir);
    }

    private TestContext CreateFreshCase(string caseName)
    {
        var caseDir = _fixture.CreateCaseDirectory(caseName);
        var stateFilePath = SessionWorkflowTemplateFixture.GetStateFilePath(caseDir);
        var log = new AppLog(Path.Combine(caseDir, "case.log"));
        var store = new SessionSnapshotStore(stateFilePath, log);
        var coordinator = CreateCoordinator(store, log, caseDir);
        coordinator.Initialize();

        return new TestContext(coordinator, store, log, caseDir);
    }

    [Fact]
    public Task LoadMedia_ThenReopen_ReusesArtifact()
    {
        var ctx = CreateFreshCase(nameof(LoadMedia_ThenReopen_ReusesArtifact));

        ctx.Coordinator.LoadMedia(_fixture.TestMediaPath);

        var coordinator2 = CreateCoordinator(ctx.Store, ctx.Log, ctx.CaseDir);
        coordinator2.Initialize();

        Assert.Equal(SessionWorkflowStage.MediaLoaded, coordinator2.CurrentSession.Stage);
        Assert.NotNull(coordinator2.CurrentSession.SourceMediaPath);
        Assert.Equal(_fixture.TestMediaPath, coordinator2.CurrentSession.SourceMediaPath);
        Assert.DoesNotContain("missing", coordinator2.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);

        return Task.CompletedTask;
    }

    [Fact]
    public Task ReopenWithMissingArtifact_SurfacesDegradedState()
    {
        var ctx = CreateFreshCase(nameof(ReopenWithMissingArtifact_SurfacesDegradedState));

        ctx.Coordinator.LoadMedia(_fixture.TestMediaPath);

        var ingestedPath = ctx.Coordinator.CurrentSession.IngestedMediaPath;
        Assert.NotNull(ingestedPath);

        if (File.Exists(ingestedPath))
        {
            File.Delete(ingestedPath);
        }

        var coordinator2 = CreateCoordinator(ctx.Store, ctx.Log, ctx.CaseDir);
        coordinator2.Initialize();

        // Stage must be downgraded — ingested media is missing so media must be re-loaded
        Assert.Equal(SessionWorkflowStage.Foundation, coordinator2.CurrentSession.Stage);
        Assert.Null(coordinator2.CurrentSession.IngestedMediaPath);

        return Task.CompletedTask;
    }

    [Fact]
    [Trait("Category", "RequiresFfmpeg")]
    [Trait("Category", "RequiresPython")]
    public async Task TranscribeMedia_ThenReopen_ReusesTranscript()
    {
        var ctx = await OpenCaseFromTemplateAsync("transcribed", nameof(TranscribeMedia_ThenReopen_ReusesTranscript));

        Assert.Equal(SessionWorkflowStage.Transcribed, ctx.Coordinator.CurrentSession.Stage);
        Assert.NotNull(ctx.Coordinator.CurrentSession.TranscriptPath);
        Assert.True(File.Exists(ctx.Coordinator.CurrentSession.TranscriptPath));
        Assert.DoesNotContain("missing", ctx.Coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "RequiresFfmpeg")]
    [Trait("Category", "RequiresPython")]
    public async Task ReopenWithMissingTranscript_SurfacesDegradedState()
    {
        var ctx = await OpenCaseFromTemplateAsync("transcribed", nameof(ReopenWithMissingTranscript_SurfacesDegradedState));

        Assert.NotNull(ctx.Coordinator.CurrentSession.TranscriptPath);

        // Patch the snapshot to reference a non-existent file rather than deleting
        // the real AppData artifact — deleting it would corrupt the shared template.
        ctx.Store.Save(ctx.Coordinator.CurrentSession with { TranscriptPath = Path.Combine(ctx.CaseDir, "nonexistent.json") });

        var coordinator2 = CreateCoordinator(ctx.Store, ctx.Log, ctx.CaseDir);
        coordinator2.Initialize();

        // Stage must be downgraded — transcript is missing so transcription must re-run
        Assert.Equal(SessionWorkflowStage.MediaLoaded, coordinator2.CurrentSession.Stage);
        Assert.Null(coordinator2.CurrentSession.TranscriptPath);
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    [Trait("Category", "RequiresExternalTranslation")]
    public async Task TranslateTranscript_ThenReopen_ReusesTranslation()
    {
        var ctx = await OpenCaseFromTemplateAsync("translated", nameof(TranslateTranscript_ThenReopen_ReusesTranslation));

        Assert.Equal(SessionWorkflowStage.Translated, ctx.Coordinator.CurrentSession.Stage);
        Assert.NotNull(ctx.Coordinator.CurrentSession.TranslationPath);
        Assert.True(File.Exists(ctx.Coordinator.CurrentSession.TranslationPath));
        Assert.DoesNotContain("missing", ctx.Coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("es", ctx.Coordinator.CurrentSession.SourceLanguage);
        Assert.Equal("en", ctx.Coordinator.CurrentSession.TargetLanguage);
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    [Trait("Category", "RequiresExternalTranslation")]
    public async Task ReopenWithMissingTranslation_SurfacesDegradedState()
    {
        var ctx = await OpenCaseFromTemplateAsync("translated", nameof(ReopenWithMissingTranslation_SurfacesDegradedState));

        Assert.NotNull(ctx.Coordinator.CurrentSession.TranslationPath);

        // Patch the snapshot to reference a non-existent file rather than deleting
        // the real AppData artifact — deleting it would corrupt the shared template.
        ctx.Store.Save(ctx.Coordinator.CurrentSession with { TranslationPath = Path.Combine(ctx.CaseDir, "nonexistent.json") });

        var coordinator2 = CreateCoordinator(ctx.Store, ctx.Log, ctx.CaseDir);
        coordinator2.Initialize();

        // Stage must be downgraded — translation is missing so translation must re-run
        Assert.Equal(SessionWorkflowStage.Transcribed, coordinator2.CurrentSession.Stage);
        Assert.Null(coordinator2.CurrentSession.TranslationPath);
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    public async Task GenerateTts_ThenReopen_ReusesAudio()
    {
        var ctx = await OpenCaseFromTemplateAsync("tts", nameof(GenerateTts_ThenReopen_ReusesAudio));

        Assert.Equal(SessionWorkflowStage.TtsGenerated, ctx.Coordinator.CurrentSession.Stage);
        Assert.NotNull(ctx.Coordinator.CurrentSession.TtsPath);
        Assert.True(File.Exists(ctx.Coordinator.CurrentSession.TtsPath));
        Assert.NotNull(ctx.Coordinator.CurrentSession.TtsSegmentsPath);
        Assert.NotNull(ctx.Coordinator.CurrentSession.TtsSegmentAudioPaths);
        Assert.DoesNotContain("missing", ctx.Coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    public async Task ReopenWithMissingTts_SurfacesDegradedState()
    {
        var ctx = await OpenCaseFromTemplateAsync("tts", nameof(ReopenWithMissingTts_SurfacesDegradedState));

        var ttsPath = ctx.Coordinator.CurrentSession.TtsPath;
        Assert.NotNull(ttsPath);

        File.Delete(ttsPath);

        var coordinator2 = CreateCoordinator(ctx.Store, ctx.Log, ctx.CaseDir);
        coordinator2.Initialize();

        // Stage must be downgraded — TTS is missing so TTS must re-run
        Assert.Equal(SessionWorkflowStage.Translated, coordinator2.CurrentSession.Stage);
        Assert.Null(coordinator2.CurrentSession.TtsPath);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EndToEndPipeline_SmokeTest()
    {
        var ctx = CreateFreshCase(nameof(EndToEndPipeline_SmokeTest));

        ctx.Coordinator.LoadMedia(_fixture.TestMediaPath);
        await ctx.Coordinator.TranscribeMediaAsync();
        await ctx.Coordinator.TranslateTranscriptAsync(targetLanguage: "en", sourceLanguage: "es");
        await ctx.Coordinator.GenerateTtsAsync();

        Assert.Equal(SessionWorkflowStage.TtsGenerated, ctx.Coordinator.CurrentSession.Stage);
        Assert.True(File.Exists(ctx.Coordinator.CurrentSession.TranscriptPath!));
        Assert.True(File.Exists(ctx.Coordinator.CurrentSession.TranslationPath!));
        Assert.True(File.Exists(ctx.Coordinator.CurrentSession.TtsPath!));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TranscribeMediaAsync_PersistsDetectedLanguage()
    {
        var ctx = await OpenCaseFromTemplateAsync("transcribed", nameof(TranscribeMediaAsync_PersistsDetectedLanguage));

        Assert.NotNull(ctx.Coordinator.CurrentSession.SourceLanguage);
        Assert.NotEmpty(ctx.Coordinator.CurrentSession.SourceLanguage);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TranslateTranscriptAsync_NoSourceLangParam_UsesSessionLanguage()
    {
        // "transcribed" template has SourceLanguage set (after fix); translate without specifying source.
        var ctx = await OpenCaseFromTemplateAsync("transcribed", nameof(TranslateTranscriptAsync_NoSourceLangParam_UsesSessionLanguage));

        var detectedLang = ctx.Coordinator.CurrentSession.SourceLanguage;
        Assert.NotNull(detectedLang);

        await ctx.Coordinator.TranslateTranscriptAsync();

        Assert.Equal(SessionWorkflowStage.Translated, ctx.Coordinator.CurrentSession.Stage);
        Assert.NotNull(ctx.Coordinator.CurrentSession.TranslationPath);
        Assert.True(File.Exists(ctx.Coordinator.CurrentSession.TranslationPath));
        Assert.Equal(detectedLang, ctx.Coordinator.CurrentSession.SourceLanguage);
        Assert.Equal("en", ctx.Coordinator.CurrentSession.TargetLanguage);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RegenerateSegmentTranslation_ActuallyWritesNewTextToSegment()
    {
        var ctx = await OpenCaseFromTemplateAsync("translated", nameof(RegenerateSegmentTranslation_ActuallyWritesNewTextToSegment));

        var translationPath = ctx.Coordinator.CurrentSession.TranslationPath!;
        var jsonBefore = await File.ReadAllTextAsync(translationPath);
        var dataBefore = JsonSerializer.Deserialize<JsonElement>(jsonBefore);
        var firstSeg = dataBefore.GetProperty("segments")[0];
        var segmentId = firstSeg.GetProperty("id").GetString()!;

        const string sentinel = "CORRUPTED_SENTINEL_DO_NOT_PERSIST";
        using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            RewriteJsonWithCorruptedSegment(dataBefore, segmentId, sentinel, writer);
        }

        await File.WriteAllBytesAsync(translationPath, ms.ToArray());

        await ctx.Coordinator.RegenerateSegmentTranslationAsync(segmentId);

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

    internal static async Task WaitUntilPlayingAsync(FakeMediaTransport player)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!player.IsPlaying && DateTime.UtcNow < timeout)
        {
            await Task.Yield();
        }
    }

    internal static async Task WaitUntilPausedAsync(FakeMediaTransport player)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!player.IsPaused && DateTime.UtcNow < timeout)
        {
            await Task.Yield();
        }
    }

    public sealed class TestContextFactory(SessionWorkflowTemplateFixture fixture)
    {
        internal async Task<TestContext> CreateCaseWithFakePlayersAsync(string caseName)
        {
            var templateDir = await fixture.GetPreparedTemplateAsync("tts");
            var caseDir = fixture.CreateCaseDirectory(caseName);
            SessionWorkflowTemplateFixture.CopyDirectory(templateDir, caseDir);

            var stateFilePath = SessionWorkflowTemplateFixture.GetStateFilePath(caseDir);
            var log = new AppLog(Path.Combine(caseDir, "case.log"));
            var store = new SessionSnapshotStore(stateFilePath, log);

            var fakeSegmentPlayer = new FakeMediaTransport();
            var fakeSourcePlayer = new FakeMediaTransport();
            var coordinator = CreateCoordinator(store, log, caseDir, fakeSegmentPlayer, fakeSourcePlayer);
            coordinator.Initialize();

            return new TestContext(coordinator, store, log, caseDir, fakeSourcePlayer, fakeSegmentPlayer);
        }
    }
}

public sealed class FakeMediaTransport : IMediaTransport, IDisposable
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
public sealed class EmbeddedPlaybackTests(SessionWorkflowTemplateFixture fixture) : IAsyncLifetime
{
    private readonly SessionWorkflowTemplateFixture _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private static SessionWorkflowCoordinator CreateCoordinator(
        SessionSnapshotStore store,
        AppLog log,
        string caseDir,
        IMediaTransport? segmentPlayer = null,
        IMediaTransport? sourcePlayer = null,
        Babel.Player.Services.Settings.AppSettings? settings = null,
        Babel.Player.Services.Registries.IDiarizationRegistry? diarizationRegistry = null)
    {
        settings ??= new Babel.Player.Services.Settings.AppSettings();
        var perSessionStore = new PerSessionSnapshotStore(Path.Combine(caseDir, "sessions"), log);
        var recentStore = new RecentSessionsStore(Path.Combine(caseDir, "recent-sessions.json"), log);

        var transcriptionRegistry = new Babel.Player.Services.Registries.TranscriptionRegistry(log);
        var translationRegistry = new Babel.Player.Services.Registries.TranslationRegistry(log);
        var ttsRegistry = new Babel.Player.Services.Registries.TtsRegistry(log);
        diarizationRegistry ??= FakeDiarizationFactory.CreateDefaultRegistry();
        var audioProcessingService = new FfmpegAudioProcessingService(log);

        return new SessionWorkflowCoordinator(
            store,
            log,
            settings,
            perSessionStore,
            recentStore,
            transcriptionRegistry,
            translationRegistry,
            ttsRegistry,
            diarizationRegistry: diarizationRegistry,
            segmentPlayer: segmentPlayer,
            sourcePlayer: sourcePlayer,
            audioProcessingService: audioProcessingService);
    }

    private static SessionWorkflowCoordinator CreateCoordinator(
        SessionSnapshotStore store,
        AppLog log,
        string caseDir,
        Babel.Player.Services.Settings.AppSettings settings,
        Babel.Player.Services.Registries.IDiarizationRegistry diarizationRegistry) =>
        CreateCoordinator(store, log, caseDir, null, null, settings, diarizationRegistry);

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

        await SessionWorkflowTests.WaitUntilPlayingAsync(sourcePlayer);

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
        
        await SessionWorkflowTests.WaitUntilPlayingAsync(sourcePlayer);
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
        IMediaTransport? sourcePlayer = null,
        Babel.Player.Services.Settings.AppSettings? settings = null,
        Babel.Player.Services.Registries.IDiarizationRegistry? diarizationRegistry = null)
    {
        settings ??= new Babel.Player.Services.Settings.AppSettings();
        var perSessionStore = new PerSessionSnapshotStore(Path.Combine(caseDir, "sessions"), log);
        var recentStore = new RecentSessionsStore(Path.Combine(caseDir, "recent-sessions.json"), log);

        var transcriptionRegistry = new Babel.Player.Services.Registries.TranscriptionRegistry(log);
        var translationRegistry = new Babel.Player.Services.Registries.TranslationRegistry(log);
        var ttsRegistry = new Babel.Player.Services.Registries.TtsRegistry(log);
        diarizationRegistry ??= FakeDiarizationFactory.CreateDefaultRegistry();
        var audioProcessingService = new FfmpegAudioProcessingService(log);

        return new SessionWorkflowCoordinator(
            store,
            log,
            settings,
            perSessionStore,
            recentStore,
            transcriptionRegistry,
            translationRegistry,
            ttsRegistry,
            diarizationRegistry: diarizationRegistry,
            segmentPlayer: segmentPlayer,
            sourcePlayer: sourcePlayer,
            audioProcessingService: audioProcessingService);
    }

    private static SessionWorkflowCoordinator CreateCoordinator(
        SessionSnapshotStore store,
        AppLog log,
        string caseDir,
        Babel.Player.Services.Settings.AppSettings settings,
        Babel.Player.Services.Registries.IDiarizationRegistry diarizationRegistry) =>
        CreateCoordinator(store, log, caseDir, null, null, settings, diarizationRegistry);

    private static EmbeddedPlaybackViewModel CreatePlaybackVm(Babel.Player.Services.Registries.IDiarizationRegistry? diarizationRegistry = null)
    {
        var caseDir = Path.Combine(Path.GetTempPath(), $"inspect-test-{Guid.NewGuid():N}");
        var log = new AppLog(Path.Combine(Path.GetTempPath(), $"inspect-test-{Guid.NewGuid():N}.log"));
        var storePath = Path.Combine(Path.GetTempPath(), $"inspect-test-{Guid.NewGuid():N}.json");
        var store = new SessionSnapshotStore(storePath, log);
        var coordinator = CreateCoordinator(store, log, caseDir, diarizationRegistry: diarizationRegistry);
        coordinator.Initialize();
        return new EmbeddedPlaybackViewModel(coordinator);
    }

    private static (EmbeddedPlaybackViewModel playback, FakeMediaTransport segmentPlayer, FakeMediaTransport sourcePlayer)
        CreatePlaybackVmWithFakePlayers()
    {
        var caseDir = Path.Combine(Path.GetTempPath(), $"inspect-test-{Guid.NewGuid():N}");
        var log = new AppLog(Path.Combine(Path.GetTempPath(), $"inspect-test-{Guid.NewGuid():N}.log"));
        var storePath = Path.Combine(Path.GetTempPath(), $"inspect-test-{Guid.NewGuid():N}.json");
        var store = new SessionSnapshotStore(storePath, log);
        var segmentPlayer = new FakeMediaTransport();
        var sourcePlayer = new FakeMediaTransport();
        var coordinator = CreateCoordinator(
            store,
            log,
            caseDir,
            segmentPlayer: segmentPlayer,
            sourcePlayer: sourcePlayer);
        coordinator.Initialize();
        coordinator.GetOrCreateSourcePlayer();
        return (new EmbeddedPlaybackViewModel(coordinator), segmentPlayer, sourcePlayer);
    }

    [Fact]
    public void EmbeddedPlaybackViewModel_DiarizationProviderOptions_ShowOffNeMoAndWeSpeaker()
    {
        var playback = CreatePlaybackVm();

        Assert.Equal(
            [string.Empty, ProviderNames.NemoLocal, ProviderNames.WeSpeakerLocal],
            playback.DiarizationProviderOptions);
    }

    [Fact]
    public void EmbeddedPlaybackViewModel_DiarizationProviderOptions_UseRegistryOnly()
    {
        var playback = CreatePlaybackVm(new FakeDiarizationRegistry());

        Assert.Equal([string.Empty], playback.DiarizationProviderOptions);
    }

    [Fact]
    public void EmbeddedPlaybackViewModel_DiarizationProvider_UpdatesSettingsAndStatus()
    {
        using var playback = CreatePlaybackVm();

        playback.DiarizationProvider = ProviderNames.WeSpeakerLocal;

        Assert.Equal(ProviderNames.WeSpeakerLocal, playback.Coordinator.CurrentSettings.DiarizationProvider);
        Assert.Contains("WeSpeaker", playback.AutoSpeakerDetectionStatus, StringComparison.Ordinal);
        Assert.True(
            playback.AutoSpeakerDetectionStatus.StartsWith("Checking ", StringComparison.Ordinal) ||
            playback.AutoSpeakerDetectionStatus.StartsWith("Speaker diarization is enabled via ", StringComparison.Ordinal),
            $"Unexpected diarization status: {playback.AutoSpeakerDetectionStatus}");
    }

    [Fact]
    public void EmbeddedPlaybackViewModel_DiarizationProvider_EmptyValueReturnsManualStatus()
    {
        var playback = CreatePlaybackVm();

        playback.DiarizationProvider = string.Empty;

        Assert.Equal(string.Empty, playback.Coordinator.CurrentSettings.DiarizationProvider);
        Assert.Equal("Manual speaker mapping is the default release flow.", playback.AutoSpeakerDetectionStatus);
    }

    [Fact]
    public void EmbeddedPlaybackViewModel_RunDiarizationOnlyCommand_RequiresProviderAndTranscribedStage()
    {
        var playback = CreatePlaybackVm();

        Assert.False(playback.RunDiarizationOnlyCommand.CanExecute(null));

        playback.Coordinator.CurrentSession = playback.Coordinator.CurrentSession with
        {
            Stage = SessionWorkflowStage.Transcribed,
        };

        Assert.True(playback.RunDiarizationOnlyCommand.CanExecute(null));

        playback.DiarizationProvider = string.Empty;

        Assert.False(playback.RunDiarizationOnlyCommand.CanExecute(null));
    }

    [Fact]
    public async Task EmbeddedPlaybackViewModel_RunDiarizationOnlyCommand_WhenAssignmentsChange_ResetsPipelineToTranslated()
    {
        var caseDir = Path.Combine(Path.GetTempPath(), $"inspect-test-{Guid.NewGuid():N}");
        var log = new AppLog(Path.Combine(Path.GetTempPath(), $"inspect-test-{Guid.NewGuid():N}.log"));
        var storePath = Path.Combine(Path.GetTempPath(), $"inspect-test-{Guid.NewGuid():N}.json");
        var store = new SessionSnapshotStore(storePath, log);
        var settings = new Babel.Player.Services.Settings.AppSettings
        {
            DiarizationProvider = ProviderNames.NemoLocal,
        };
        var fakeProvider = new FakeDiarizationProvider(_ =>
            new DiarizationResult(
                true,
                [new DiarizedSegment(0.0, 1.0, "spk_02")],
                1,
                null));
        var fakeRegistry = new FakeDiarizationRegistry((ProviderNames.NemoLocal, "NeMo", fakeProvider));
        var coordinator = CreateCoordinator(store, log, caseDir, settings, fakeRegistry);
        coordinator.Initialize();

        var transcriptPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(transcriptPath, """{"language":"es","segments":[{"start":0.0,"end":1.0,"text":"hola"}]}""");
        var translationPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(translationPath, """{"sourceLanguage":"es","targetLanguage":"en","segments":[{"id":"segment_0.0","start":0.0,"end":1.0,"text":"hola","translatedText":"hello"}]}""");
        var mediaPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(mediaPath, "audio");
        var ttsPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(ttsPath, "tts");

        coordinator.CurrentSession = coordinator.CurrentSession with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
            TtsPath = ttsPath,
        };

        var playback = new EmbeddedPlaybackViewModel(coordinator);

        Assert.True(playback.RunDiarizationOnlyCommand.CanExecute(null));

        await playback.RunDiarizationOnlyCommand.ExecuteAsync(null);

        var translation = await ArtifactJson.LoadTranslationAsync(translationPath);

        Assert.Equal(SessionWorkflowStage.Translated, coordinator.CurrentSession.Stage);
        Assert.Null(coordinator.CurrentSession.TtsPath);
        Assert.Equal("spk_02", translation.Segments![0].SpeakerId);
        Assert.Single(playback.Segments);
        Assert.Contains("reset to translated state", playback.StatusText, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void SpeechRate_OnlyUpdatesTtsSegmentPlaybackRate()
    {
        var (playback, segmentPlayer, sourcePlayer) = CreatePlaybackVmWithFakePlayers();

        playback.SpeechRate = 1.4;

        Assert.Equal(1.4, playback.Coordinator.TtsPlaybackRate);
        Assert.Equal(1.0, sourcePlayer.PlaybackRate);
        Assert.Equal(1.0, segmentPlayer.PlaybackRate);
    }

    [Fact]
    public void TranslationRuntime_SwitchToLocal_ResolvesProviderAndModel()
    {
        var playback = CreatePlaybackVm();

        // Switch to Cloud first so we can test switching back to CPU.
        playback.TranslationRuntime = ComputeProfile.Cloud;
        playback.TranslationProvider = ProviderNames.OpenAi;
        playback.TranslationModel = "gpt-4o-mini";

        playback.TranslationRuntime = ComputeProfile.Cpu;

        Assert.Equal(ComputeProfile.Cpu, playback.TranslationRuntime);
        Assert.Equal(ProviderNames.CTranslate2, playback.TranslationProvider);
        Assert.Equal("nllb-200-distilled-600M", playback.TranslationModel);
        Assert.Equal(ComputeProfile.Cpu, playback.Coordinator.CurrentSettings.TranslationProfile);
        Assert.Equal(ProviderNames.CTranslate2, playback.Coordinator.CurrentSettings.TranslationProvider);
        Assert.Equal("nllb-200-distilled-600M", playback.Coordinator.CurrentSettings.TranslationModel);
    }

    [Fact]
    public void TtsRuntime_SwitchToLocal_ResolvesProviderAndVoice()
    {
        var playback = CreatePlaybackVm();

        // Switch to Cloud first so we can test switching back to CPU.
        playback.TtsRuntime = ComputeProfile.Cloud;
        playback.TtsModelOrVoice = "eleven_multilingual_v2";
        playback.TtsRuntime = ComputeProfile.Cpu;

        Assert.Equal(ComputeProfile.Cpu, playback.TtsRuntime);
        Assert.Equal(ProviderNames.Piper, playback.TtsProvider);
        Assert.Equal("en_US-lessac-medium", playback.TtsModelOrVoice);
        Assert.Equal(ComputeProfile.Cpu, playback.Coordinator.CurrentSettings.TtsProfile);
        Assert.Equal(ProviderNames.Piper, playback.Coordinator.CurrentSettings.TtsProvider);
        Assert.Equal("en_US-lessac-medium", playback.Coordinator.CurrentSettings.TtsVoice);
    }

    [Fact]
    public void TranscriptionRuntime_SwitchToContainerized_UpdatesCoordinatorSettings()
    {
        var playback = CreatePlaybackVm();

        playback.TranscriptionRuntime = ComputeProfile.Gpu;

        Assert.Equal(ComputeProfile.Gpu, playback.TranscriptionRuntime);
        Assert.Equal(ProviderNames.FasterWhisper, playback.TranscriptionProvider);
        Assert.Equal("tiny", playback.TranscriptionModel);
        Assert.Equal(ComputeProfile.Gpu, playback.Coordinator.CurrentSettings.TranscriptionProfile);
        Assert.Equal(ProviderNames.FasterWhisper, playback.Coordinator.CurrentSettings.TranscriptionProvider);
        Assert.Equal("tiny", playback.Coordinator.CurrentSettings.TranscriptionModel);
    }

    [Fact]
    public void TranslationProvider_Change_ReconcilesModelToProviderDefault()
    {
        var playback = CreatePlaybackVm();

        // Switch to Cloud first so Cloud providers are available.
        playback.TranslationRuntime = ComputeProfile.Cloud;
        playback.TranslationProvider = ProviderNames.OpenAi;
        playback.TranslationModel = "gpt-4o-mini";

        playback.TranslationProvider = ProviderNames.GoogleTranslateFree;

        Assert.Equal(ProviderNames.GoogleTranslateFree, playback.TranslationProvider);
        Assert.Equal("default", playback.TranslationModel);
        Assert.Equal(ProviderNames.GoogleTranslateFree, playback.Coordinator.CurrentSettings.TranslationProvider);
        Assert.Equal("default", playback.Coordinator.CurrentSettings.TranslationModel);
    }

    [Fact]
    public void TtsProvider_Change_ReconcilesVoiceToProviderDefault()
    {
        var playback = CreatePlaybackVm();

        // Switch to Cloud first so Cloud providers are available.
        playback.TtsRuntime = ComputeProfile.Cloud;
        playback.TtsProvider = ProviderNames.ElevenLabs;
        playback.TtsModelOrVoice = "eleven_multilingual_v2";

        playback.TtsProvider = ProviderNames.EdgeTts;

        Assert.Equal(ProviderNames.EdgeTts, playback.TtsProvider);
        Assert.Equal("en-US-AriaNeural", playback.TtsModelOrVoice);
        Assert.Equal(ProviderNames.EdgeTts, playback.Coordinator.CurrentSettings.TtsProvider);
        Assert.Equal("en-US-AriaNeural", playback.Coordinator.CurrentSettings.TtsVoice);
    }

    // Regression guard: RefreshSegmentsAsync uses _isUpdatingActiveSegment to prevent
    // Avalonia's TwoWay ListBox binding from re-selecting a segment (and triggering
    // SeekAndPlayAsync) when the Segments collection is replaced.
    [Fact]
    public async Task RefreshSegments_WhileSegmentIsSelected_DoesNotTriggerSourcePlayback()
    {
        var (playback, _, sourcePlayer) = CreatePlaybackVmWithFakePlayers();

        // Pre-select a segment while IsSourceMediaLoaded is false so OnSelectedSegmentChanged
        // exits early without calling SeekAndPlayAsync.
        var seg = new WorkflowSegmentState("segment_2.0", 2.0, 4.0, "test", false, null, false);
        playback.SelectedSegment = seg;

        // Mark media as loaded — from this point any non-guarded SelectedSegment change
        // with a non-null value would call SeekAndPlayAsync on the source player.
        playback.IsSourceMediaLoaded = true;

        var segments = new List<WorkflowSegmentState>
        {
            new("segment_0.0", 0.0, 2.0, "p1", true, "t1", true),
            new("segment_2.0", 2.0, 4.0, "test", true, "t2", true),
            new("segment_4.0", 4.0, 6.0, "p2", true, "t3", true)
        };

        // This call replaces the internal Segments collection.
        // If not properly guarded, Avalonia would re-select the segment and trigger playback.
        await playback.RefreshSegmentsAsync(segments);

        Assert.Equal(0, sourcePlayer.LastSeekPosition);
        Assert.False(sourcePlayer.IsPlaying);
    }
}
