using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class PipelineStageProgressTests : IDisposable
{
    private readonly string _dir;
    private readonly string _storePath;
    private readonly string _perSessionDir;
    private readonly string _recentPath;
    private readonly string _mediaPath;

    public PipelineStageProgressTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-pipeline-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _storePath = Path.Combine(_dir, "session.json");
        _perSessionDir = Path.Combine(_dir, "sessions");
        _recentPath = Path.Combine(_dir, "recent-sessions.json");
        Directory.CreateDirectory(_perSessionDir);

        _mediaPath = Path.Combine(_dir, "sample.mp4");
        File.WriteAllText(_mediaPath, "fake media");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public async Task AdvancePipelineAsync_FreshRun_EmitsSequentialVerboseStageUpdates()
    {
        var settings = CreateSettings();
        var coordinator = CreateCoordinator(
            settings,
            new FakeTranscriptionRegistry(new FakeTranscriptionProvider()),
            new FakeTranslationRegistry(new FakeTranslationProvider()),
            new FakeTtsRegistry(new FakeTtsProvider()));
        coordinator.Initialize();
        coordinator.LoadMedia(_mediaPath);

        var updates = new List<SessionWorkflowCoordinator.PipelineStageUpdate>();
        await coordinator.AdvancePipelineAsync(stageProgress: new CaptureProgress<SessionWorkflowCoordinator.PipelineStageUpdate>(updates));

        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        AssertStage(updates, SessionWorkflowStage.Transcribed, 1, 3);
        AssertStage(updates, SessionWorkflowStage.Translated, 2, 3);
        AssertStage(updates, SessionWorkflowStage.TtsGenerated, 3, 3);

        var transcriptionIndex = updates.FindIndex(u => u.TargetStage == SessionWorkflowStage.Transcribed);
        var translationIndex = updates.FindIndex(u => u.TargetStage == SessionWorkflowStage.Translated);
        var dubIndex = updates.FindIndex(u => u.TargetStage == SessionWorkflowStage.TtsGenerated);
        Assert.True(transcriptionIndex >= 0, "Expected at least one transcription update.");
        Assert.True(translationIndex > transcriptionIndex, "Translation updates should start after transcription updates.");
        Assert.True(dubIndex > translationIndex, "Dub updates should start after translation updates.");
    }

    [Fact]
    public async Task AdvancePipelineAsync_FromTranslatedSession_EmitsOnlyDubAsOneOfOne()
    {
        var settings = CreateSettings();
        var transcriptionRegistry = new FakeTranscriptionRegistry(new FakeTranscriptionProvider());
        var translationRegistry = new FakeTranslationRegistry(new FakeTranslationProvider());
        var ttsRegistry = new FakeTtsRegistry(new FakeTtsProvider());

        var coordinator = CreateCoordinator(settings, transcriptionRegistry, translationRegistry, ttsRegistry);
        coordinator.Initialize();
        coordinator.LoadMedia(_mediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync();

        coordinator = CreateCoordinator(settings, transcriptionRegistry, translationRegistry, ttsRegistry);
        coordinator.Initialize();

        var updates = new List<SessionWorkflowCoordinator.PipelineStageUpdate>();
        await coordinator.AdvancePipelineAsync(stageProgress: new CaptureProgress<SessionWorkflowCoordinator.PipelineStageUpdate>(updates));

        Assert.NotEmpty(updates);
        Assert.All(updates, update =>
        {
            Assert.Equal(SessionWorkflowStage.TtsGenerated, update.TargetStage);
            Assert.Equal(1, update.StageIndex);
            Assert.Equal(1, update.StageCount);
        });
        Assert.Contains(updates, update => update.Progress01 == 1d && !update.IsIndeterminate);
    }

    [Fact]
    public async Task AdvancePipelineAsync_ModelDownloadProgress_IsMappedIntoActiveStageBar()
    {
        var settings = CreateSettings();
        var downloadProvider = new FakeTranslationProvider(
            requiresDownload: true,
            downloadSteps: [0.25, 0.5, 1.0]);
        var coordinator = CreateCoordinator(
            settings,
            new FakeTranscriptionRegistry(new FakeTranscriptionProvider()),
            new FakeTranslationRegistry(downloadProvider),
            new FakeTtsRegistry(new FakeTtsProvider()));
        coordinator.Initialize();
        coordinator.LoadMedia(_mediaPath);

        var updates = new List<SessionWorkflowCoordinator.PipelineStageUpdate>();
        await coordinator.AdvancePipelineAsync(stageProgress: new CaptureProgress<SessionWorkflowCoordinator.PipelineStageUpdate>(updates));

        var translationUpdates = updates
            .Where(update => update.TargetStage == SessionWorkflowStage.Translated)
            .ToList();
        Assert.Contains(
            translationUpdates,
            update => !update.IsIndeterminate &&
                      Math.Abs(update.Progress01 - 0.25) < 0.001 &&
                      update.Detail.Contains("Preparing translation model", StringComparison.Ordinal));
        Assert.Contains(
            translationUpdates,
            update => !update.IsIndeterminate &&
                      Math.Abs(update.Progress01 - 0.5) < 0.001 &&
                      update.StageIndex == 2 &&
                      update.StageCount == 3);
    }

    [Fact]
    public void EmbeddedPlaybackViewModel_DubLabelStaysConstantAndVerboseProgressStateUpdates()
    {
        var settings = CreateSettings();
        var coordinator = CreateCoordinator(
            settings,
            new FakeTranscriptionRegistry(new FakeTranscriptionProvider()),
            new FakeTranslationRegistry(new FakeTranslationProvider()),
            new FakeTtsRegistry(new FakeTtsProvider()));
        coordinator.Initialize();
        var playback = new EmbeddedPlaybackViewModel(coordinator);

        Assert.Equal("🎙 Dub", playback.DubModeLabel);
        playback.IsDubModeOn = true;
        Assert.Equal("🎙 Dub", playback.DubModeLabel);

        playback.ApplyPipelineStageUpdate(
            new SessionWorkflowCoordinator.PipelineStageUpdate(
                2,
                3,
                SessionWorkflowStage.Translated,
                "Translation",
                "Checking translation runtime, provider readiness, language routing, and model availability…",
                0.45,
                false));

        Assert.True(playback.IsPipelineProgressVisible);
        Assert.Equal("Stage 2 of 3: Translation", playback.PipelineStageTitle);
        Assert.Contains("language routing", playback.PipelineStageDetail, StringComparison.Ordinal);
        Assert.False(playback.IsPipelineProgressIndeterminate);
        Assert.Equal(0.45, playback.PipelineProgressPercent, 3);
        Assert.Contains("45", playback.PipelineProgressStatusLine, StringComparison.Ordinal);

        playback.ShowPipelineRefreshDetail("Loading segments and refreshing playback data…");
        Assert.Equal("Loading segments and refreshing playback data…", playback.PipelineStageDetail);
        Assert.True(playback.IsPipelineProgressIndeterminate);

        playback.ResetPipelineProgressState();
        Assert.False(playback.IsPipelineProgressVisible);
        Assert.Equal(string.Empty, playback.PipelineStageTitle);
        Assert.Equal(string.Empty, playback.PipelineStageDetail);
    }

    private SessionWorkflowCoordinator CreateCoordinator(
        AppSettings settings,
        ITranscriptionRegistry transcriptionRegistry,
        ITranslationRegistry translationRegistry,
        ITtsRegistry ttsRegistry)
    {
        var log = new AppLog(Path.Combine(_dir, $"test-{Guid.NewGuid():N}.log"));
        var store = new SessionSnapshotStore(_storePath, log);
        var perSessionStore = new PerSessionSnapshotStore(_perSessionDir, log);
        var recentStore = new RecentSessionsStore(_recentPath, log);
        return new SessionWorkflowCoordinator(
            store,
            log,
            settings,
            perSessionStore,
            recentStore,
            transcriptionRegistry,
            translationRegistry,
            ttsRegistry);
    }

    private static AppSettings CreateSettings() =>
        new()
        {
            TranscriptionProfile = ComputeProfile.Cpu,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "fake-whisper",
            TranslationProfile = ComputeProfile.Cpu,
            TranslationProvider = ProviderNames.CTranslate2,
            TranslationModel = "fake-translation-model",
            TtsProfile = ComputeProfile.Cloud,
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "fake-voice",
            TargetLanguage = "en",
        };

    private static void AssertStage(
        IReadOnlyList<SessionWorkflowCoordinator.PipelineStageUpdate> updates,
        SessionWorkflowStage stage,
        int expectedIndex,
        int expectedCount)
    {
        var stageUpdates = updates.Where(update => update.TargetStage == stage).ToList();
        Assert.NotEmpty(stageUpdates);
        Assert.All(stageUpdates, update =>
        {
            Assert.Equal(expectedIndex, update.StageIndex);
            Assert.Equal(expectedCount, update.StageCount);
        });
        Assert.Contains(stageUpdates, update => update.IsIndeterminate && update.Progress01 == 0d);
        Assert.Contains(stageUpdates, update => !update.IsIndeterminate && update.Progress01 == 1d);
    }

    private sealed class CaptureProgress<T>(List<T> values) : IProgress<T>
    {
        public void Report(T value) => values.Add(value);
    }

    private sealed class FakeTranscriptionRegistry : ITranscriptionRegistry
    {
        private readonly FakeTranscriptionProvider _provider;

        public FakeTranscriptionRegistry(FakeTranscriptionProvider provider)
        {
            _provider = provider;
        }

        public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null) =>
        [
            new ProviderDescriptor(
                ProviderNames.FasterWhisper,
                "Fake transcription",
                false,
                null,
                ["fake-whisper"],
                SupportedRuntimes: [InferenceRuntime.Local],
                DefaultRuntime: InferenceRuntime.Local)
        ];

        public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
            ["fake-whisper"];

        public ITranscriptionProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null) =>
            _provider;

        public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null) =>
            _provider.CheckReadiness(settings, keyStore);

        public Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null) =>
            _provider.EnsureReadyAsync(settings, progress, ct);
    }

    private sealed class FakeTranslationRegistry : ITranslationRegistry
    {
        private readonly FakeTranslationProvider _provider;

        public FakeTranslationRegistry(FakeTranslationProvider provider)
        {
            _provider = provider;
        }

        public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null) =>
        [
            new ProviderDescriptor(
                ProviderNames.CTranslate2,
                "Fake translation",
                false,
                null,
                ["fake-translation-model"],
                SupportedRuntimes: [InferenceRuntime.Local],
                DefaultRuntime: InferenceRuntime.Local)
        ];

        public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
            ["fake-translation-model"];

        public ITranslationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null) =>
            _provider;

        public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null) =>
            _provider.CheckReadiness(settings, keyStore);

        public Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null) =>
            _provider.EnsureReadyAsync(settings, progress, ct);
    }

    private sealed class FakeTtsRegistry : ITtsRegistry
    {
        private readonly FakeTtsProvider _provider;

        public FakeTtsRegistry(FakeTtsProvider provider)
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
                ["fake-voice"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud)
        ];

        public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
            ["fake-voice"];

        public ITtsProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null) =>
            _provider;

        public ProviderReadiness CheckReadiness(string providerId, string modelOrVoice, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null) =>
            _provider.CheckReadiness(settings, keyStore);

        public Task<bool> EnsureModelAsync(string providerId, string modelOrVoice, AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null) =>
            _provider.EnsureReadyAsync(settings, progress, ct);
    }

    private sealed class FakeTranscriptionProvider : ITranscriptionProvider
    {
        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
            ProviderReadiness.Ready;

        public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputJsonPath)!);
            var artifact = new TranscriptArtifact
            {
                Language = "es",
                LanguageProbability = 0.99,
                Segments =
                [
                    new TranscriptSegmentArtifact { Start = 0.0, End = 1.1, Text = "Hola" },
                    new TranscriptSegmentArtifact { Start = 1.1, End = 2.4, Text = "Mundo" },
                ],
            };
            File.WriteAllText(request.OutputJsonPath, ArtifactJson.SerializeTranscript(artifact));
            return Task.FromResult<TranscriptionResult>(
                new(
                    true,
                    [
                        new TranscriptSegment(0.0, 1.1, "Hola"),
                        new TranscriptSegment(1.1, 2.4, "Mundo"),
                    ],
                    "es",
                    0.99,
                    null));
        }
    }

    private sealed class FakeTranslationProvider : ITranslationProvider
    {
        private bool _requiresDownload;
        private readonly IReadOnlyList<double> _downloadSteps;

        public FakeTranslationProvider(bool requiresDownload = false, IReadOnlyList<double>? downloadSteps = null)
        {
            _requiresDownload = requiresDownload;
            _downloadSteps = downloadSteps ?? [];
        }

        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
            _requiresDownload
                ? new ProviderReadiness(
                    false,
                    $"Model '{settings.TranslationModel}' not downloaded yet.",
                    RequiresModelDownload: true,
                    ModelDownloadDescription: $"Download {settings.TranslationModel}")
                : ProviderReadiness.Ready;

        public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default)
        {
            foreach (var step in _downloadSteps)
            {
                progress?.Report(step);
            }

            _requiresDownload = false;
            return Task.FromResult(true);
        }

        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
        {
            var transcript = await ArtifactJson.LoadTranscriptAsync(request.TranscriptJsonPath, cancellationToken);
            var segments = transcript.Segments?.Select(segment => new TranslationSegmentArtifact
            {
                Id = SessionWorkflowCoordinator.SegmentId(segment.Start),
                Start = segment.Start,
                End = segment.End,
                Text = segment.Text,
                TranslatedText = $"{segment.Text} (en)",
            }).ToList() ?? [];

            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputJsonPath)!);
            File.WriteAllText(
                request.OutputJsonPath,
                ArtifactJson.SerializeTranslation(
                    new TranslationArtifact
                    {
                        SourceLanguage = request.SourceLanguage,
                        TargetLanguage = request.TargetLanguage,
                        Segments = segments,
                    }));

            return new TranslationResult(
                true,
                segments.Select(segment => new TranslatedSegment(
                    segment.Start,
                    segment.End,
                    segment.Text ?? string.Empty,
                    segment.TranslatedText ?? string.Empty)).ToList(),
                request.SourceLanguage,
                request.TargetLanguage,
                null);
        }

        public async Task<TranslationResult> TranslateSingleSegmentAsync(SingleSegmentTranslationRequest request, CancellationToken cancellationToken = default)
        {
            var translation = await ArtifactJson.LoadTranslationAsync(request.TranslationJsonPath, cancellationToken);
            foreach (var segment in translation.Segments ?? [])
            {
                if (segment.Id == request.SegmentId)
                {
                    segment.TranslatedText = $"{request.SourceText} (en)";
                }
            }

            File.WriteAllText(request.OutputJsonPath, ArtifactJson.SerializeTranslation(translation));
            return new TranslationResult(
                true,
                (translation.Segments ?? [])
                    .Select(segment => new TranslatedSegment(
                        segment.Start,
                        segment.End,
                        segment.Text ?? string.Empty,
                        segment.TranslatedText ?? string.Empty))
                    .ToList(),
                translation.SourceLanguage ?? request.SourceLanguage,
                translation.TargetLanguage ?? request.TargetLanguage,
                null);
        }
    }

    private sealed class FakeTtsProvider : ITtsProvider
    {
        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
            ProviderReadiness.Ready;

        public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<TtsResult> GenerateTtsAsync(TtsRequest request, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputAudioPath)!);
            File.WriteAllText(request.OutputAudioPath, "fake audio");
            return Task.FromResult(new TtsResult(true, request.OutputAudioPath, request.VoiceName, new FileInfo(request.OutputAudioPath).Length, null));
        }

        public Task<TtsResult> GenerateSegmentTtsAsync(SingleSegmentTtsRequest request, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputAudioPath)!);
            File.WriteAllText(request.OutputAudioPath, request.Text);
            return Task.FromResult(new TtsResult(true, request.OutputAudioPath, request.VoiceName, new FileInfo(request.OutputAudioPath).Length, null));
        }
    }
}
