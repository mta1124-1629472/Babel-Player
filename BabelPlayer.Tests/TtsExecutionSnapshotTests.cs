using System;
using System.IO;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

public sealed class TtsExecutionSnapshotTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;
    private readonly SessionSnapshotStore _store;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;
    private readonly AppSettings _settings;

    public TtsExecutionSnapshotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-tts-snapshot-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "tts-snapshot.log"));
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
            // Best-effort temp cleanup only.
        }
    }

    [Fact]
    public async Task GenerateTtsAsync_UsesFrozenSnapshotTimingWhenSettingsChangeMidRun()
    {
        _settings.DubTimingMode = SegmentTimingMode.Stretch;
        var audioProcessing = new FakeAudioProcessingService
        {
            TimeStretchShouldSucceed = true,
        };
        var ttsProvider = new BlockingFakeTtsProvider();
        var coordinator = CreateCoordinator(audioProcessing, ttsProvider);
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
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var translationPath = await SessionSemanticsIntegrityFixture.WriteTranslationAsync(_dir, transcriptPath, template);

        coordinator.CurrentSession = template with
        {
            Stage = SessionWorkflowStage.Translated,
            SourceMediaPath = mediaPath,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
        };

        var generateTask = coordinator.GenerateTtsAsync();
        await ttsProvider.SegmentStarted.Task;

        _settings.DubTimingMode = SegmentTimingMode.Off;
        ttsProvider.AllowSegmentCompletion.TrySetResult(true);

        await generateTask;

        Assert.Equal(SegmentTimingMode.Off, _settings.DubTimingMode);
        Assert.Equal(1, audioProcessing.TimeStretchCallCount);
        Assert.True(audioProcessing.ComposeTimelineDubAsyncCalled);
        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        Assert.Equal(SegmentTimingMode.Stretch, coordinator.CurrentSession.DubTimingMode);
        Assert.True(coordinator.CurrentSession.TtsSettingsDriftedSinceArtifact);
        Assert.NotNull(coordinator.CurrentSession.TtsPath);
        Assert.True(File.Exists(coordinator.CurrentSession.TtsPath));
    }

    private SessionWorkflowCoordinator CreateCoordinator(
        IAudioProcessingService audioProcessingService,
        ITtsProvider ttsProvider)
    {
        var registries = new RegistryBundle(
            _perSessionStore,
            _recentStore,
            new FakeTranscriptionRegistry(),
            new FakeTranslationRegistry(),
            new FakeTtsRegistry(ttsProvider));

        return new SessionWorkflowCoordinator(
            new CoordinatorCoreServices(_store, _log, _settings),
            registries,
            new CoordinatorOptions
            {
                AudioProcessingService = audioProcessingService,
            });
    }

    private sealed class BlockingFakeTtsProvider : ITtsProvider
    {
        public TaskCompletionSource<bool> SegmentStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> AllowSegmentCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TtsResult> GenerateTtsAsync(TtsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("BlockingFakeTtsProvider only supports per-segment generation.");

        public async Task<TtsResult> GenerateSegmentTtsAsync(SingleSegmentTtsRequest request, CancellationToken cancellationToken = default)
        {
            SegmentStarted.TrySetResult(true);
            await AllowSegmentCompletion.Task.WaitAsync(cancellationToken);
            await File.WriteAllBytesAsync(request.OutputAudioPath, [0x01, 0x02, 0x03], cancellationToken);
            return new TtsResult(true, request.OutputAudioPath, request.VoiceName, 3, null);
        }

        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) => new(true, "Ready");

        public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}
