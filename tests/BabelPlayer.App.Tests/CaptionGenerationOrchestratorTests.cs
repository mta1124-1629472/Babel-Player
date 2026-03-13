using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

public sealed class CaptionGenerationOrchestratorTests
{
    [Fact]
    public void CanBeConstructedFromDependencies()
    {
        var orchestrator = CreateOrchestrator();

        Assert.NotNull(orchestrator);
    }

    [Fact]
    public async Task StartCallsCaptionGenerator()
    {
        var generator = new FakeCaptionGenerator();
        var orchestrator = CreateOrchestrator(generator: generator);

        await orchestrator.StartAutomaticCaptionGenerationAsync("C:\\test.mp4", CancellationToken.None);

        Assert.Equal(1, generator.CallCount);
    }

    [Fact]
    public async Task StartPopulatesMediaSessionWithGeneratedCues()
    {
        var cues = new List<SubtitleCue>
        {
            new() { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), SourceText = "Hello" }
        };
        var generator = new FakeCaptionGenerator(cues);
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var orchestrator = CreateOrchestrator(generator: generator, coordinator: coordinator);

        var result = await orchestrator.StartAutomaticCaptionGenerationAsync("C:\\test.mp4", CancellationToken.None);

        Assert.True(result.UsedGeneratedCaptions);
        Assert.True(coordinator.Snapshot.Transcript.Segments.Count > 0);
    }

    [Fact]
    public async Task CancellationStopsInFlightWork()
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<SubtitleCue>>();
        var generator = new BlockingCaptionGenerator(tcs);
        var orchestrator = CreateOrchestrator(generator: generator);

        using var cts = new CancellationTokenSource();
        var task = orchestrator.StartAutomaticCaptionGenerationAsync("C:\\test.mp4", cts.Token);

        orchestrator.CancelCaptionGeneration();
        tcs.TrySetCanceled();

        var result = await task;
        Assert.NotNull(result);
    }

    [Fact]
    public void CancelCaptionGenerationClearsCaptionGenerationState()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var orchestrator = CreateOrchestrator(coordinator: coordinator);

        coordinator.SetCaptionGenerationState(true);
        orchestrator.CancelCaptionGeneration();

        Assert.False(coordinator.Snapshot.Transcript.IsGenerating);
    }

    [Fact]
    public void TryLoadCachedReturnsFalseWhenCacheEmpty()
    {
        var orchestrator = CreateOrchestrator();

        Assert.False(orchestrator.TryLoadCachedGeneratedSubtitles("C:\\test.mp4", SubtitleWorkflowCatalog.DefaultTranscriptionModelKey));
    }

    [Fact]
    public async Task CacheRoundTripsGeneratedSubtitles()
    {
        var cues = new List<SubtitleCue>
        {
            new() { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), SourceText = "Hello" }
        };
        var generator = new FakeCaptionGenerator(cues);
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var orchestrator = CreateOrchestrator(generator: generator, coordinator: coordinator);

        await orchestrator.StartAutomaticCaptionGenerationAsync("C:\\test.mp4", CancellationToken.None);

        Assert.True(orchestrator.TryLoadCachedGeneratedSubtitles("C:\\test.mp4", SubtitleWorkflowCatalog.DefaultTranscriptionModelKey));
        Assert.True(coordinator.Snapshot.Transcript.Segments.Count > 0);
    }

    [Fact]
    public async Task StartUpdatesWorkflowState()
    {
        var generator = new FakeCaptionGenerator();
        var workflowStore = new InMemorySubtitleWorkflowStateStore();
        var orchestrator = CreateOrchestrator(generator: generator, workflowStore: workflowStore);

        await orchestrator.StartAutomaticCaptionGenerationAsync("C:\\test.mp4", CancellationToken.None);

        Assert.Equal("C:\\test.mp4", workflowStore.Snapshot.CurrentVideoPath);
        Assert.Equal(1, workflowStore.Snapshot.ActiveCaptionGenerationId);
    }

    [Fact]
    public async Task StartNotifiesHostOfStatus()
    {
        var host = new FakeCaptionGenerationHost();
        var orchestrator = CreateOrchestrator(host: host);

        await orchestrator.StartAutomaticCaptionGenerationAsync("C:\\test.mp4", CancellationToken.None);

        Assert.True(host.PublishStatusCalls > 0);
    }

    private static CaptionGenerationOrchestrator CreateOrchestrator(
        ICaptionGenerator? generator = null,
        MediaSessionCoordinator? coordinator = null,
        InMemorySubtitleWorkflowStateStore? workflowStore = null,
        ICaptionGenerationHost? host = null)
    {
        coordinator ??= new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        workflowStore ??= new InMemorySubtitleWorkflowStateStore();
        return new CaptionGenerationOrchestrator(
            generator ?? new FakeCaptionGenerator(),
            coordinator,
            workflowStore,
            host ?? new FakeCaptionGenerationHost(),
            NullBabelLogFactory.Instance.CreateLogger("test"));
    }

    private sealed class FakeCaptionGenerator : ICaptionGenerator
    {
        private readonly IReadOnlyList<SubtitleCue> _cues;

        public FakeCaptionGenerator(IReadOnlyList<SubtitleCue>? cues = null)
        {
            _cues = cues ?? [];
        }

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<SubtitleCue>> GenerateCaptionsAsync(
            string videoPath,
            TranscriptionModelSelection selection,
            string? languageHint,
            Action<TranscriptChunk>? onFinal,
            Action<ModelTransferProgress>? onProgress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_cues);
        }
    }

    private sealed class BlockingCaptionGenerator : ICaptionGenerator
    {
        private readonly TaskCompletionSource<IReadOnlyList<SubtitleCue>> _tcs;

        public BlockingCaptionGenerator(TaskCompletionSource<IReadOnlyList<SubtitleCue>> tcs)
        {
            _tcs = tcs;
        }

        public Task<IReadOnlyList<SubtitleCue>> GenerateCaptionsAsync(
            string videoPath,
            TranscriptionModelSelection selection,
            string? languageHint,
            Action<TranscriptChunk>? onFinal,
            Action<ModelTransferProgress>? onProgress,
            CancellationToken cancellationToken)
        {
            return _tcs.Task;
        }
    }

    private sealed class FakeCaptionGenerationHost : ICaptionGenerationHost
    {
        public int PublishStatusCalls { get; private set; }
        public int TranslateCalls { get; private set; }

        public void ApplyAutomaticTranslationPreferenceIfNeeded() { }
        public void CancelTranslationWork() { }
        public void InitializeTranslationPreferencesForNewVideo() { }

        public void PublishStatus(string message, string? overlayStatus)
        {
            PublishStatusCalls++;
        }

        public Task TranslateCueAsync(TranscriptSegment cue, CancellationToken cancellationToken)
        {
            TranslateCalls++;
            return Task.CompletedTask;
        }
    }
}
