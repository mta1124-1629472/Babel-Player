using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

/// <summary>
/// Negative/error-path tests across orchestrators.
/// Ensures graceful failure, no state corruption, and meaningful exceptions.
/// </summary>
public sealed class ErrorPathTests
{
    // ── CaptionGenerationOrchestrator ─────────────────────────────────────────

    [Fact]
    public async Task CaptionGenerationOrchestrator_ProviderThrows_DoesNotLeaveGeneratingStateActive()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var workflowStore = new InMemorySubtitleWorkflowStateStore();
        var orchestrator = new CaptionGenerationOrchestrator(
            new ThrowingCaptionGenerator(),
            coordinator,
            workflowStore,
            new FakeGenerationHost(),
            NullBabelLogFactory.Instance.CreateLogger("test"));

        await orchestrator.StartAutomaticCaptionGenerationAsync("C:\\video.mp4", CancellationToken.None);

        Assert.False(coordinator.Snapshot.Transcript.IsGenerating);
    }

    [Fact]
    public async Task CaptionGenerationOrchestrator_ProviderThrows_DoesNotPopulateSegments()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var workflowStore = new InMemorySubtitleWorkflowStateStore();
        var orchestrator = new CaptionGenerationOrchestrator(
            new ThrowingCaptionGenerator(),
            coordinator,
            workflowStore,
            new FakeGenerationHost(),
            NullBabelLogFactory.Instance.CreateLogger("test"));

        await orchestrator.StartAutomaticCaptionGenerationAsync("C:\\video.mp4", CancellationToken.None);

        Assert.Empty(coordinator.Snapshot.Transcript.Segments);
    }

    [Fact]
    public async Task CaptionGenerationOrchestrator_EmptyResult_ProducesNoSegments()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var workflowStore = new InMemorySubtitleWorkflowStateStore();
        var orchestrator = new CaptionGenerationOrchestrator(
            new EmptyCaptionGenerator(),
            coordinator,
            workflowStore,
            new FakeGenerationHost(),
            NullBabelLogFactory.Instance.CreateLogger("test"));

        var result = await orchestrator.StartAutomaticCaptionGenerationAsync("C:\\video.mp4", CancellationToken.None);

        Assert.False(result.UsedGeneratedCaptions);
        Assert.Empty(coordinator.Snapshot.Transcript.Segments);
    }

    [Fact]
    public async Task CaptionGenerationOrchestrator_Cancellation_DoesNotCorruptState()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var workflowStore = new InMemorySubtitleWorkflowStateStore();
        var tcs = new TaskCompletionSource<IReadOnlyList<SubtitleCue>>();
        var orchestrator = new CaptionGenerationOrchestrator(
            new BlockingDelegateCaptionGenerator(() => tcs.Task),
            coordinator,
            workflowStore,
            new FakeGenerationHost(),
            NullBabelLogFactory.Instance.CreateLogger("test"));

        var task = orchestrator.StartAutomaticCaptionGenerationAsync("C:\\video.mp4", CancellationToken.None);
        orchestrator.CancelCaptionGeneration();
        tcs.TrySetCanceled();

        await task;

        // After cancellation, generation flag must be cleared
        Assert.False(coordinator.Snapshot.Transcript.IsGenerating);
    }

    // ── MediaSessionCoordinator edge cases ────────────────────────────────────

    [Fact]
    public void MediaSessionCoordinator_UpsertSegment_DoesNotLosePreviousSegments()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        coordinator.SetTranscriptSegments(
        [
            new TranscriptSegment { Id = new TranscriptSegmentId("tr:1"), Start = TimeSpan.FromSeconds(0), End = TimeSpan.FromSeconds(2), Text = "First", Language = "en" },
            new TranscriptSegment { Id = new TranscriptSegmentId("tr:2"), Start = TimeSpan.FromSeconds(5), End = TimeSpan.FromSeconds(7), Text = "Second", Language = "en" }
        ], SubtitlePipelineSource.Sidecar, "en");

        // Upsert a brand-new segment
        coordinator.UpsertTranscriptSegment(new TranscriptSegment
        {
            Id = new TranscriptSegmentId("tr:3"),
            Start = TimeSpan.FromSeconds(10),
            End = TimeSpan.FromSeconds(12),
            Text = "Third",
            Language = "en"
        });

        Assert.Equal(3, coordinator.Snapshot.Transcript.Segments.Count);
    }

    [Fact]
    public void MediaSessionCoordinator_ReplaceTranslationSegments_WithEmptyList_ClearsAll()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        coordinator.UpsertTranslationSegment(new TranslationSegment
        {
            Id = new TranslationSegmentId("tl:1"),
            SourceSegmentId = new TranscriptSegmentId("tr:1"),
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "Hello",
            Language = "en"
        });

        coordinator.ReplaceTranslationSegments([]);

        Assert.Empty(coordinator.Snapshot.Translation.Segments);
    }

    // ── SubtitleWorkflowController error paths ────────────────────────────────

    [Fact]
    public async Task SubtitleWorkflowController_LoadMediaSubtitles_WithNoSidecar_DoesNotThrow()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var videoPath = Path.Combine(directory.FullName, "nosidecar.mp4");
            File.WriteAllText(videoPath, string.Empty);

            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(new FakeCredentialStore()),
                environmentVariableReader: _ => null);

            // Should not throw even with no sidecar
            var result = await controller.LoadMediaSubtitlesAsync(videoPath);
            Assert.False(result.UsedSidecar);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitleWorkflowController_InitializeAsync_WithNoCredentials_DoesNotThrow()
    {
        var controller = TestWorkflowControllerFactory.Create(
            new CredentialFacade(new FakeCredentialStore()),
            environmentVariableReader: _ => null);

        // Initialization with no credentials should complete without throwing
        await controller.InitializeAsync();
        Assert.NotNull(controller.Snapshot);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class ThrowingCaptionGenerator : ICaptionGenerator
    {
        public Task<IReadOnlyList<SubtitleCue>> GenerateCaptionsAsync(
            string videoPath,
            TranscriptionModelSelection selection,
            string? languageHint,
            Action<TranscriptChunk>? onFinal,
            Action<ModelTransferProgress>? onProgress,
            CancellationToken cancellationToken)
        {
            return Task.FromException<IReadOnlyList<SubtitleCue>>(
                new InvalidOperationException("Caption generator failed."));
        }
    }

    private sealed class EmptyCaptionGenerator : ICaptionGenerator
    {
        public Task<IReadOnlyList<SubtitleCue>> GenerateCaptionsAsync(
            string videoPath,
            TranscriptionModelSelection selection,
            string? languageHint,
            Action<TranscriptChunk>? onFinal,
            Action<ModelTransferProgress>? onProgress,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<SubtitleCue>>([]);
        }
    }

    private sealed class BlockingDelegateCaptionGenerator : ICaptionGenerator
    {
        private readonly Func<Task<IReadOnlyList<SubtitleCue>>> _factory;

        public BlockingDelegateCaptionGenerator(Func<Task<IReadOnlyList<SubtitleCue>>> factory)
        {
            _factory = factory;
        }

        public Task<IReadOnlyList<SubtitleCue>> GenerateCaptionsAsync(
            string videoPath,
            TranscriptionModelSelection selection,
            string? languageHint,
            Action<TranscriptChunk>? onFinal,
            Action<ModelTransferProgress>? onProgress,
            CancellationToken cancellationToken)
        {
            return _factory();
        }
    }

    private sealed class FakeGenerationHost : ICaptionGenerationHost
    {
        public void ApplyAutomaticTranslationPreferenceIfNeeded() { }
        public void CancelTranslationWork() { }
        public void InitializeTranslationPreferencesForNewVideo() { }
        public void PublishStatus(string message, string? overlayStatus) { }
        public Task TranslateCueAsync(TranscriptSegment cue, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public string? GetOpenAiApiKey() => null;
        public void SaveOpenAiApiKey(string apiKey) { }
        public string? GetGoogleTranslateApiKey() => null;
        public void SaveGoogleTranslateApiKey(string apiKey) { }
        public string? GetDeepLApiKey() => null;
        public void SaveDeepLApiKey(string apiKey) { }
        public string? GetMicrosoftTranslatorApiKey() => null;
        public void SaveMicrosoftTranslatorApiKey(string apiKey) { }
        public string? GetMicrosoftTranslatorRegion() => null;
        public void SaveMicrosoftTranslatorRegion(string region) { }
        public string? GetSubtitleModelKey() => null;
        public void SaveSubtitleModelKey(string modelKey) { }
        public string? GetTranslationModelKey() => null;
        public void SaveTranslationModelKey(string modelKey) { }
        public void ClearTranslationModelKey() { }
        public bool GetAutoTranslateEnabled() => false;
        public void SaveAutoTranslateEnabled(bool enabled) { }
        public string? GetLlamaCppServerPath() => null;
        public void SaveLlamaCppServerPath(string path) { }
        public string? GetLlamaCppRuntimeVersion() => null;
        public void SaveLlamaCppRuntimeVersion(string version) { }
        public string? GetLlamaCppRuntimeSource() => null;
        public void SaveLlamaCppRuntimeSource(string source) { }
    }
}
