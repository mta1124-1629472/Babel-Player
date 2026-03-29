using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Babel.Deck.Models;
using Babel.Deck.Services;

namespace BabelDeck.Tests;

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

    private async Task<(SessionWorkflowCoordinator coordinator, SessionSnapshotStore store, AppLog log, string caseDir)>
        OpenCaseFromTemplateAsync(string templateName, string caseName)
    {
        var templateDir = await _fixture.GetPreparedTemplateAsync(templateName);
        var caseDir = _fixture.CreateCaseDirectory(caseName);

        SessionWorkflowTemplateFixture.CopyDirectory(templateDir, caseDir);

        var stateFilePath = SessionWorkflowTemplateFixture.GetStateFilePath(caseDir);
        var log = new AppLog(Path.Combine(caseDir, "case.log"));
        var store = new SessionSnapshotStore(stateFilePath, log);
        var coordinator = new SessionWorkflowCoordinator(store, log);
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
        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        return (coordinator, store, log, caseDir);
    }

    [Fact]
    public Task LoadMedia_ThenReopen_ReusesArtifact()
    {
        var (coordinator, store, log, _) = CreateFreshCase(nameof(LoadMedia_ThenReopen_ReusesArtifact));

        coordinator.LoadMedia(_fixture.TestMediaPath);

        coordinator = new SessionWorkflowCoordinator(store, log);
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
        var (coordinator, store, log, _) = CreateFreshCase(nameof(ReopenWithMissingArtifact_SurfacesDegradedState));

        coordinator.LoadMedia(_fixture.TestMediaPath);

        var ingestedPath = coordinator.CurrentSession.IngestedMediaPath;
        Assert.NotNull(ingestedPath);

        if (File.Exists(ingestedPath))
        {
            File.Delete(ingestedPath);
        }

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Contains("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);

        return Task.CompletedTask;
    }

    [Fact]
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
    public async Task ReopenWithMissingTranscript_SurfacesDegradedState()
    {
        var (coordinator, store, log, _) =
            await OpenCaseFromTemplateAsync("transcribed", nameof(ReopenWithMissingTranscript_SurfacesDegradedState));

        var transcriptPath = coordinator.CurrentSession.TranscriptPath;
        Assert.NotNull(transcriptPath);

        File.Delete(transcriptPath);

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Contains("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
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
    public async Task ReopenWithMissingTranslation_SurfacesDegradedState()
    {
        var (coordinator, store, log, _) =
            await OpenCaseFromTemplateAsync("translated", nameof(ReopenWithMissingTranslation_SurfacesDegradedState));

        var translationPath = coordinator.CurrentSession.TranslationPath;
        Assert.NotNull(translationPath);

        File.Delete(translationPath);

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Contains("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
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
    public async Task ReopenWithMissingTts_SurfacesDegradedState()
    {
        var (coordinator, store, log, _) =
            await OpenCaseFromTemplateAsync("tts", nameof(ReopenWithMissingTts_SurfacesDegradedState));

        var ttsPath = coordinator.CurrentSession.TtsPath;
        Assert.NotNull(ttsPath);

        File.Delete(ttsPath);

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Contains("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EndToEndPipeline_SmokeTest()
    {
        var caseDir = _fixture.CreateCaseDirectory(nameof(EndToEndPipeline_SmokeTest));
        var stateFilePath = SessionWorkflowTemplateFixture.GetStateFilePath(caseDir);
        var log = new AppLog(Path.Combine(caseDir, "case.log"));
        var store = new SessionSnapshotStore(stateFilePath, log);
        var coordinator = new SessionWorkflowCoordinator(store, log);

        coordinator.Initialize();
        coordinator.LoadMedia(_fixture.TestMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");
        await coordinator.GenerateTtsAsync();

        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        Assert.True(File.Exists(coordinator.CurrentSession.TranscriptPath!));
        Assert.True(File.Exists(coordinator.CurrentSession.TranslationPath!));
        Assert.True(File.Exists(coordinator.CurrentSession.TtsPath!));
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

    public event EventHandler? Ended;
    public event EventHandler<Exception>? ErrorOccurred;

    public void Load(string filePath) { LastLoadedFile = filePath; IsPlaying = false; IsPaused = true; HasEnded = false; }
    public void Play() { IsPlaying = true; IsPaused = false; }
    public void Pause() { IsPlaying = false; IsPaused = true; }
    public void Seek(long positionMs) { LastSeekPosition = positionMs; }
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
        var coordinator = new SessionWorkflowCoordinator(store, log, fakeSegmentPlayer, fakeSourcePlayer);
        coordinator.Initialize();

        return (coordinator, fakeSourcePlayer, caseDir);
    }

    [Fact]
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
    public async Task PlaySourceMediaAtSegment_InvalidSegment_Throws()
    {
        var (coordinator, _, _) = await CreateCaseWithSourcePlayerAsync(
            nameof(PlaySourceMediaAtSegment_InvalidSegment_Throws));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.PlaySourceMediaAtSegmentAsync("nonexistent-segment-id"));
    }

    [Fact]
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
        var coordinator = new SessionWorkflowCoordinator(store, log, sourcePlayer: fakeSource);
        coordinator.Initialize();

        // Fresh session has no media loaded
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.PlaySourceMediaAtSegmentAsync("any-id"));
    }
}
