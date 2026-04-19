using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Orchestration;
using Babel.Player.Services.Planning;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.Services.Transcription;

namespace BabelPlayer.Tests;

public sealed class ExtractedOrchestratorTests
{
    [Fact]
    public async Task TranscriptionOrchestrator_ExecuteAsync_CommitsTranscriptStateOnSuccess()
    {
        using var scope = new TestScope();
        var mediaPath = scope.CreateFile("sample.mp4");
        var settings = CreateSettings();
        var session = new FakeSessionStateAccessor(
            settings,
            scope.SessionDirectory,
            WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
            {
                IngestedMediaPath = mediaPath,
            });
        var planner = new FakeStageExecutionPlanner();
        var providers = new FakeProviderLifecycleManager();
        var committer = new FakeSessionCommitter();
        var engine = new FakeInferenceExecutionEngine
        {
            TranscribeAsyncImpl = (_, request, _) => Task.FromResult(
                new TranscriptionResult(
                    true,
                    [new TranscriptSegment(0, 1, "hello world")],
                    "en",
                    0.99,
                    null)),
        };
        var orchestrator = new TranscriptionOrchestrator(
            session,
            planner,
            providers,
            committer,
            engine,
            scope.Log);

        await orchestrator.ExecuteAsync(progress: null, stageContext: null, CancellationToken.None);

        Assert.Single(planner.RequestedStages);
        Assert.Equal(InferenceStage.Transcription, planner.RequestedStages[0]);
        Assert.NotNull(committer.TranscriptionCommit);
        Assert.EndsWith(
            Path.Combine("transcripts", "sample.json.work"),
            committer.TranscriptionCommit!.Value.TranscriptPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("sample.mp4", Path.GetFileName(session.CurrentSession.IngestedMediaPath));
        Assert.Equal(1, providers.CreateTranscriptionServiceCalls);
        Assert.NotNull(providers.TranscriptionService);
    }

    [Fact]
    public async Task TranscriptionOrchestrator_ExecuteAsync_UsesSeparatedVocalsWhenEnabled()
    {
        using var scope = new TestScope();
        var mediaPath = scope.CreateFile("original.mp4");
        var vocalsPath = scope.CreateFile("vocals.wav");
        var settings = CreateSettings();
        settings.VocalSeparationEnabled = true;
        var session = new FakeSessionStateAccessor(
            settings,
            scope.SessionDirectory,
            WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
            {
                IngestedMediaPath = mediaPath,
            });
        var providers = new FakeProviderLifecycleManager
        {
            SeparateVocalsAsyncImpl = (_, _, _) => Task.FromResult(vocalsPath),
        };
        var committer = new FakeSessionCommitter();
        var engine = new FakeInferenceExecutionEngine
        {
            TranscribeAsyncImpl = (_, request, _) => Task.FromResult(
                new TranscriptionResult(
                    true,
                    [new TranscriptSegment(0, 1, "hello world")],
                    "en",
                    0.99,
                    null)),
        };
        var orchestrator = new TranscriptionOrchestrator(
            session,
            new FakeStageExecutionPlanner(),
            providers,
            committer,
            engine,
            scope.Log);

        await orchestrator.ExecuteAsync(progress: null, stageContext: null, CancellationToken.None);

        Assert.NotNull(engine.LastTranscriptionRequest);
        Assert.Equal(vocalsPath, engine.LastTranscriptionRequest!.SourceAudioPath);
        Assert.NotNull(committer.TranscriptionCommit);
        Assert.EndsWith(
            Path.Combine("transcripts", "original.json.work"),
            committer.TranscriptionCommit!.Value.TranscriptPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscriptionOrchestrator_ExecuteAsync_RetriesRecoverableCpuFailureWithSafeFallback()
    {
        using var scope = new TestScope();
        var mediaPath = scope.CreateFile("sample.mp4");
        var settings = CreateSettings();
        settings.TranscriptionProvider = ProviderNames.FasterWhisper;
        settings.TranscriptionCpuComputeType = "int8_float16";
        settings.TranscriptionCpuThreads = 8;
        settings.TranscriptionNumWorkersUseAuto = false;
        settings.TranscriptionNumWorkers = 4;
        var session = new FakeSessionStateAccessor(
            settings,
            scope.SessionDirectory,
            WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
            {
                IngestedMediaPath = mediaPath,
            });
        var engine = new FakeInferenceExecutionEngine();
        var committer = new FakeSessionCommitter();
        var callCount = 0;
        engine.TranscribeAsyncImpl = (_, request, _) =>
        {
            callCount++;
            return Task.FromResult(
                callCount == 1
                    ? new TranscriptionResult(false, [], string.Empty, 0, "out of memory")
                    : new TranscriptionResult(
                        true,
                        [new TranscriptSegment(0, 1, "hello world")],
                        "en",
                        0.99,
                        null));
        };
        var orchestrator = new TranscriptionOrchestrator(
            session,
            new FakeStageExecutionPlanner(),
            new FakeProviderLifecycleManager(),
            committer,
            engine,
            scope.Log);

        await orchestrator.ExecuteAsync(progress: null, stageContext: null, CancellationToken.None);

        Assert.Equal(2, engine.TranscriptionRequests.Count);
        Assert.Equal("int8", engine.TranscriptionRequests[1].CpuComputeType);
        Assert.Equal(0, engine.TranscriptionRequests[1].CpuThreads);
        Assert.Equal(1, engine.TranscriptionRequests[1].NumWorkers);
        Assert.NotNull(committer.TranscriptionCommit);
    }

    [Fact]
    public async Task TranscriptionOrchestrator_ExecuteAsync_ThrowsPipelineProviderExceptionOnFailure()
    {
        using var scope = new TestScope();
        var mediaPath = scope.CreateFile("sample.mp4");
        var settings = CreateSettings();
        settings.TranscriptionProvider = ProviderNames.FasterWhisper;
        var session = new FakeSessionStateAccessor(
            settings,
            scope.SessionDirectory,
            WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
            {
                IngestedMediaPath = mediaPath,
            });
        var engine = new FakeInferenceExecutionEngine
        {
            TranscribeAsyncImpl = (_, _, _) => Task.FromResult(
                new TranscriptionResult(false, [], string.Empty, 0, "permission denied")),
        };
        var orchestrator = new TranscriptionOrchestrator(
            session,
            new FakeStageExecutionPlanner(),
            new FakeProviderLifecycleManager(),
            new FakeSessionCommitter(),
            engine,
            scope.Log);

        var ex = await Assert.ThrowsAsync<PipelineProviderException>(
            () => orchestrator.ExecuteAsync(progress: null, stageContext: null, CancellationToken.None));

        Assert.Contains("failed during transcription stage", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task TranslationOrchestrator_ExecuteAsync_NormalizesLanguagesAndCommitsState()
    {
        using var scope = new TestScope();
        var transcriptPath = scope.CreateFile("input.json", "{}");
        var settings = CreateSettings();
        var session = new FakeSessionStateAccessor(
            settings,
            scope.SessionDirectory,
            WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
            {
                TranscriptPath = transcriptPath,
                SourceLanguage = "ES",
            });
        var providers = new FakeProviderLifecycleManager();
        var committer = new FakeSessionCommitter();
        var engine = new FakeInferenceExecutionEngine
        {
            TranslateAsyncImpl = (_, request, _) => Task.FromResult(
                new TranslationResult(
                    true,
                    [new TranslatedSegment(0, 1, "hola", "hello")],
                    request.SourceLanguage,
                    request.TargetLanguage,
                    null)),
        };
        var orchestrator = new TranslationOrchestrator(
            session,
            new FakeStageExecutionPlanner(),
            providers,
            committer,
            engine,
            scope.Log);

        await orchestrator.ExecuteAsync(
            progress: null,
            targetLanguage: " EN ",
            sourceLanguage: " ES ",
            stageContext: null,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(engine.LastTranslationRequest);
        Assert.Equal("es", engine.LastTranslationRequest!.SourceLanguage);
        Assert.Equal("en", engine.LastTranslationRequest.TargetLanguage);
        Assert.Equal(1, providers.CreateTranslationServiceCalls);
        Assert.NotNull(providers.TranslationService);
        Assert.NotNull(committer.TranslationCommit);
        Assert.Equal("es", committer.TranslationCommit!.Value.Snapshot.SourceLanguage);
        Assert.Equal("en", committer.TranslationCommit.Value.Snapshot.TargetLanguage);
        Assert.EndsWith(
            Path.Combine("translations", "input_en.json.work"),
            committer.TranslationCommit.Value.Snapshot.WorkingTranslationPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranslationOrchestrator_ExecuteAsync_MapsDownloadProgressIntoStageUpdates()
    {
        using var scope = new TestScope();
        var transcriptPath = scope.CreateFile("input.json", "{}");
        var settings = CreateSettings();
        var session = new FakeSessionStateAccessor(
            settings,
            scope.SessionDirectory,
            WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
            {
                TranscriptPath = transcriptPath,
                SourceLanguage = "es",
            });
        var providers = new FakeProviderLifecycleManager
        {
            EnsureTranslationExecutionReadyAsyncImpl = (progress, _) =>
            {
                progress?.Report(0.25);
                progress?.Report(1.0);
                return Task.CompletedTask;
            },
        };
        var updates = new List<PipelineStageUpdate>();
        var stageContext = new PipelineStageContext(
            2,
            3,
            SessionWorkflowStage.Translated,
            "Translation",
            new CaptureProgress<PipelineStageUpdate>(updates.Add));
        var orchestrator = new TranslationOrchestrator(
            session,
            new FakeStageExecutionPlanner(),
            providers,
            new FakeSessionCommitter(),
            new FakeInferenceExecutionEngine
            {
                TranslateAsyncImpl = (_, request, _) => Task.FromResult(
                    new TranslationResult(
                        true,
                        [new TranslatedSegment(0, 1, "hola", "hello")],
                        request.SourceLanguage,
                        request.TargetLanguage,
                        null)),
            },
            scope.Log);

        await orchestrator.ExecuteAsync(
            progress: null,
            targetLanguage: "en",
            sourceLanguage: "es",
            stageContext,
            CancellationToken.None);

        Assert.Contains(
            updates,
            update => !update.IsIndeterminate
                   && Math.Abs(update.Progress01 - 0.25) < 0.001
                   && update.Detail.Contains("Preparing translation model", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            updates,
            update => !update.IsIndeterminate
                   && Math.Abs(update.Progress01 - 1.0) < 0.001
                   && update.Detail.Contains("Preparing translation model", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TranslationOrchestrator_ExecuteAsync_UsesCapturedSnapshotAfterSettingsMutation()
    {
        using var scope = new TestScope();
        var transcriptPath = scope.CreateFile("input.json", "{}");
        var settings = CreateSettings();
        settings.TranslationProvider = "provider-a";
        settings.TranslationModel = "model-a";
        var session = new FakeSessionStateAccessor(
            settings,
            scope.SessionDirectory,
            WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
            {
                TranscriptPath = transcriptPath,
                SourceLanguage = "es",
            });
        var providers = new FakeProviderLifecycleManager();
        providers.PrepareTranslationExecutionSnapshotAsyncImpl = (stagePlan, path, sourceLanguage, targetLanguage, _, _) =>
        {
            var provider = new StubTranslationProvider();
            var leases = new ProviderLeaseManager<ITranslationProvider>(scope.Log, "test-translation");
            var lease = leases.AcquireOrCreate(() => provider, stagePlan.ProviderId);
            session.CurrentSettings.TranslationProvider = "provider-b";
            session.CurrentSettings.TranslationModel = "model-b";
            return Task.FromResult(
                new TranslationExecutionSnapshot(
                    Guid.NewGuid(),
                    session.CurrentSession.SessionId,
                    1,
                    1,
                    stagePlan,
                    lease,
                    "model-a",
                    sourceLanguage,
                    targetLanguage,
                    path,
                    ArtifactIdentity.Capture(path),
                    Path.Combine(scope.SessionDirectory, "translations", "input_en.json"),
                    Path.Combine(scope.SessionDirectory, "translations", "input_en.json.work")));
        };
        var committer = new FakeSessionCommitter();
        var engine = new FakeInferenceExecutionEngine
        {
            TranslateAsyncImpl = (_, request, _) => Task.FromResult(
                new TranslationResult(
                    true,
                    [new TranslatedSegment(0, 1, "hola", "hello")],
                    request.SourceLanguage,
                    request.TargetLanguage,
                    null)),
        };
        var orchestrator = new TranslationOrchestrator(
            session,
            new FakeStageExecutionPlanner(),
            providers,
            committer,
            engine,
            scope.Log);

        await orchestrator.ExecuteAsync(
            progress: null,
            targetLanguage: "en",
            sourceLanguage: "es",
            stageContext: null,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(engine.LastTranslationRequest);
        Assert.Equal("model-a", engine.LastTranslationRequest!.ModelName);
        Assert.NotNull(committer.TranslationCommit);
        Assert.Equal("model-a", committer.TranslationCommit!.Value.Snapshot.Model);
        Assert.NotEqual("provider-b", committer.TranslationCommit.Value.Snapshot.Plan.ProviderId);
    }

    [Fact]
    public async Task DiarizationStageOrchestrator_ExecuteAsync_RejectsMissingMedia()
    {
        using var scope = new TestScope();
        var settings = CreateSettings();
        settings.DiarizationProvider = ProviderNames.WeSpeakerLocal;
        var session = new FakeSessionStateAccessor(
            settings,
            scope.SessionDirectory,
            WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
            {
                TranscriptPath = scope.CreateFile("input.json", "{}"),
            });
        var orchestrator = new DiarizationStageOrchestrator(
            session,
            new FakeStageExecutionPlanner(),
            new FakeDiarizationExecutor(),
            scope.Log);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.ExecuteAsync(stageContext: null, CancellationToken.None));

        Assert.Equal("No ingested media is available for speaker mapping.", ex.Message);
    }

    [Fact]
    public async Task DiarizationStageOrchestrator_ExecuteAsync_CallsExecutorAndReportsProgress()
    {
        using var scope = new TestScope();
        var settings = CreateSettings();
        settings.DiarizationProvider = ProviderNames.WeSpeakerLocal;
        var session = new FakeSessionStateAccessor(
            settings,
            scope.SessionDirectory,
            WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
            {
                IngestedMediaPath = scope.CreateFile("audio.wav"),
                TranscriptPath = scope.CreateFile("input.json", "{}"),
            });
        var updates = new List<PipelineStageUpdate>();
        var stageContext = new PipelineStageContext(
            2,
            4,
            SessionWorkflowStage.Diarized,
            "Speaker Mapping",
            new CaptureProgress<PipelineStageUpdate>(updates.Add));
        var executor = new FakeDiarizationExecutor
        {
            ExecuteAsyncImpl = (_, _, _, _, _) => Task.FromResult((true, 3, 12)),
        };
        var orchestrator = new DiarizationStageOrchestrator(
            session,
            new FakeStageExecutionPlanner(),
            executor,
            scope.Log);

        await orchestrator.ExecuteAsync(stageContext, CancellationToken.None);

        Assert.Equal(session.CurrentSession.IngestedMediaPath, executor.LastAudioPath);
        Assert.Equal(session.CurrentSession.TranscriptPath, executor.LastTranscriptPath);
        Assert.Equal(SessionWorkflowStage.Diarized, executor.LastResultingStage);
        Assert.Equal("Speaker analysis complete.", executor.LastStatusMessage);
        Assert.Contains(
            updates,
            update => update.Detail.Contains("Running", StringComparison.OrdinalIgnoreCase)
                   && update.IsIndeterminate);
        Assert.Contains(
            updates,
            update => update.Detail.Contains("Identified 3 speakers across 12 segments", StringComparison.OrdinalIgnoreCase)
                   && !update.IsIndeterminate
                   && Math.Abs(update.Progress01 - 1.0) < 0.001);
    }

    private static AppSettings CreateSettings() =>
        new()
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "fake-whisper",
            TranscriptionProfile = ComputeProfile.Cpu,
            TranslationProvider = ProviderNames.CTranslate2,
            TranslationModel = "fake-translation-model",
            TranslationProfile = ComputeProfile.Cpu,
            TargetLanguage = "en",
            DiarizationProvider = string.Empty,
        };

    private sealed class TestScope : IDisposable
    {
        public TestScope()
        {
            Root = Path.Combine(Path.GetTempPath(), $"babel-orchestrator-tests-{Guid.NewGuid():N}");
            SessionDirectory = Path.Combine(Root, "session");
            Directory.CreateDirectory(SessionDirectory);
            Log = new AppLog(Path.Combine(Root, "test.log"));
        }

        public string Root { get; }
        public string SessionDirectory { get; }
        public AppLog Log { get; }

        public string CreateFile(string name, string contents = "data")
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            Log.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private sealed class FakeSessionStateAccessor(
        AppSettings settings,
        string sessionDirectory,
        WorkflowSessionSnapshot currentSession) : ISessionStateAccessor
    {
        public WorkflowSessionSnapshot CurrentSession { get; set; } = currentSession;

        public AppSettings CurrentSettings { get; } = settings;

        public HardwareSnapshot HardwareSnapshot { get; } =
            CpuTranscriptionRuntimePolicy.CreateMinimalProbeSnapshot();

        public int SaveCalls { get; private set; }

        public string GetSessionDirectory() => sessionDirectory;

        public void SaveCurrentSession() => SaveCalls++;
    }

    private sealed class FakeStageExecutionPlanner : IStageExecutionPlanner
    {
        public List<InferenceStage> RequestedStages { get; } = [];

        public StageExecutionPlan ResolveAndApplyExecutionPlan(InferenceStage stage)
        {
            RequestedStages.Add(stage);
            var role = stage switch
            {
                InferenceStage.Diarization => RuntimeRole.CpuDiar,
                InferenceStage.Tts => RuntimeRole.CpuVoice,
                _ => RuntimeRole.CpuNlp,
            };
            return new StageExecutionPlan(
                stage,
                "fake-provider",
                InferenceRuntime.Local,
                ComputeProfile.Cpu,
                role,
                false,
                "test");
        }
    }

    private sealed class FakeProviderLifecycleManager : IProviderLifecycleManager
    {
        private readonly ProviderLeaseManager<ITranslationProvider> _translationLeases =
            new(new AppLog(Path.Combine(Path.GetTempPath(), "babel-player-tests-providerlease.log")), "test-translation");
        private ITranscriptionProvider _createdTranscriptionProvider = new StubTranscriptionProvider();
        private ITranslationProvider _createdTranslationProvider = new StubTranslationProvider();

        public ITranscriptionProvider? TranscriptionService { get; set; }

        public ITranslationProvider? TranslationService { get; set; }

        public int CreateTranscriptionServiceCalls { get; private set; }

        public int CreateTranslationServiceCalls { get; private set; }

        public Func<IProgress<double>?, PipelineStageContext?, CancellationToken, Task>? EnsureTranscriptionProviderReadyAsyncImpl { get; set; }

        public Func<IProgress<double>?, CancellationToken, Task>? EnsureTranslationExecutionReadyAsyncImpl { get; set; }

        public Func<StageExecutionPlan, string, string, string, IProgress<double>?, CancellationToken, Task<TranslationExecutionSnapshot>>? PrepareTranslationExecutionSnapshotAsyncImpl { get; set; }

        public Func<IProgress<double>?, PipelineStageContext?, CancellationToken, Task<string>>? SeparateVocalsAsyncImpl { get; set; }

        public ITranscriptionProvider CreateTranscriptionService()
        {
            CreateTranscriptionServiceCalls++;
            return _createdTranscriptionProvider;
        }

        public ITranslationProvider CreateTranslationService()
        {
            CreateTranslationServiceCalls++;
            return _createdTranslationProvider;
        }

        public Task EnsureTranscriptionProviderReadyAsync(
            IProgress<double>? progress,
            PipelineStageContext? stageContext,
            CancellationToken cancellationToken) =>
            EnsureTranscriptionProviderReadyAsyncImpl?.Invoke(progress, stageContext, cancellationToken)
            ?? Task.CompletedTask;

        public Task<TranslationExecutionSnapshot> PrepareTranslationExecutionSnapshotAsync(
            StageExecutionPlan stagePlan,
            string transcriptPath,
            string normalizedSourceLanguage,
            string normalizedTargetLanguage,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            if (PrepareTranslationExecutionSnapshotAsyncImpl is not null)
            {
                return PrepareTranslationExecutionSnapshotAsyncImpl(
                    stagePlan,
                    transcriptPath,
                    normalizedSourceLanguage,
                    normalizedTargetLanguage,
                    progress,
                    cancellationToken);
            }

            var readinessTask = EnsureTranslationExecutionReadyAsyncImpl?.Invoke(progress, cancellationToken)
                ?? Task.CompletedTask;
            if (!readinessTask.IsCompletedSuccessfully)
                return AwaitPreparedSnapshotAsync(readinessTask, stagePlan, transcriptPath, normalizedSourceLanguage, normalizedTargetLanguage);

            TranslationService ??= CreateTranslationService();
            var lease = _translationLeases.AcquireOrCreate(() => TranslationService, stagePlan.ProviderId);
            return Task.FromResult(
                new TranslationExecutionSnapshot(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    1,
                    1,
                    stagePlan,
                    lease,
                    "test-model",
                    normalizedSourceLanguage,
                    normalizedTargetLanguage,
                    transcriptPath,
                    ArtifactIdentity.Capture(transcriptPath),
                    Path.Combine(Path.GetDirectoryName(transcriptPath)!, "translations", $"{Path.GetFileNameWithoutExtension(transcriptPath)}_{normalizedTargetLanguage}.json"),
                    Path.Combine(Path.GetDirectoryName(transcriptPath)!, "translations", $"{Path.GetFileNameWithoutExtension(transcriptPath)}_{normalizedTargetLanguage}.json.work")));
        }

        private async Task<TranslationExecutionSnapshot> AwaitPreparedSnapshotAsync(
            Task readinessTask,
            StageExecutionPlan stagePlan,
            string transcriptPath,
            string normalizedSourceLanguage,
            string normalizedTargetLanguage)
        {
            await readinessTask.ConfigureAwait(false);
            TranslationService ??= CreateTranslationService();
            var lease = _translationLeases.AcquireOrCreate(() => TranslationService, stagePlan.ProviderId);
            return new TranslationExecutionSnapshot(
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                1,
                stagePlan,
                lease,
                "test-model",
                normalizedSourceLanguage,
                normalizedTargetLanguage,
                transcriptPath,
                ArtifactIdentity.Capture(transcriptPath),
                Path.Combine(Path.GetDirectoryName(transcriptPath)!, "translations", $"{Path.GetFileNameWithoutExtension(transcriptPath)}_{normalizedTargetLanguage}.json"),
                Path.Combine(Path.GetDirectoryName(transcriptPath)!, "translations", $"{Path.GetFileNameWithoutExtension(transcriptPath)}_{normalizedTargetLanguage}.json.work"));
        }

        public Task<string> SeparateVocalsAsync(
            IProgress<double>? progress,
            PipelineStageContext? stageContext,
            CancellationToken cancellationToken) =>
            SeparateVocalsAsyncImpl?.Invoke(progress, stageContext, cancellationToken)
            ?? Task.FromException<string>(new InvalidOperationException("SeparateVocalsAsync was not configured."));
    }

    private sealed class FakeSessionCommitter : ISessionCommitter
    {
        public (TranscriptionResult Result, string TranscriptPath)? TranscriptionCommit { get; private set; }

        public (TranslationExecutionSnapshot Snapshot, TranslationResult Result)? TranslationCommit { get; private set; }

        public Task CommitTranscriptionSessionStateAsync(TranscriptionResult result, string transcriptPath)
        {
            TranscriptionCommit = (result, transcriptPath);
            return Task.CompletedTask;
        }

        public Task CommitTranslationSessionStateAsync(
            TranslationExecutionSnapshot snapshot,
            TranslationResult result)
        {
            TranslationCommit = (snapshot, result);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDiarizationExecutor : IDiarizationExecutor
    {
        public string? LastAudioPath { get; private set; }
        public string? LastTranscriptPath { get; private set; }
        public SessionWorkflowStage? LastResultingStage { get; private set; }
        public string? LastStatusMessage { get; private set; }

        public Func<string, string, CancellationToken, SessionWorkflowStage?, string?, Task<(bool SpeakerAssignmentsChanged, int SpeakerCount, int SegmentCount)>>? ExecuteAsyncImpl { get; set; }

        public Task<(bool SpeakerAssignmentsChanged, int SpeakerCount, int SegmentCount)> ExecuteDiarizationAsync(
            string audioPath,
            string transcriptPath,
            CancellationToken ct,
            SessionWorkflowStage? resultingStage = null,
            string? statusMessage = null)
        {
            LastAudioPath = audioPath;
            LastTranscriptPath = transcriptPath;
            LastResultingStage = resultingStage;
            LastStatusMessage = statusMessage;
            return ExecuteAsyncImpl?.Invoke(audioPath, transcriptPath, ct, resultingStage, statusMessage)
                ?? Task.FromResult((false, 0, 0));
        }
    }

    private sealed class FakeInferenceExecutionEngine : IInferenceExecutionEngine
    {
        public Func<ITranscriptionProvider, TranscriptionRequest, CancellationToken, Task<TranscriptionResult>>? TranscribeAsyncImpl { get; set; }
        public Func<ITranslationProvider, TranslationRequest, CancellationToken, Task<TranslationResult>>? TranslateAsyncImpl { get; set; }

        public List<TranscriptionRequest> TranscriptionRequests { get; } = [];
        public TranscriptionRequest? LastTranscriptionRequest { get; private set; }
        public TranslationRequest? LastTranslationRequest { get; private set; }

        public Task<TranscriptionResult> TranscribeAsync(
            ITranscriptionProvider provider,
            TranscriptionRequest request,
            CancellationToken cancellationToken = default)
        {
            TranscriptionRequests.Add(request);
            LastTranscriptionRequest = request;
            return TranscribeAsyncImpl?.Invoke(provider, request, cancellationToken)
                ?? Task.FromException<TranscriptionResult>(new InvalidOperationException("TranscribeAsync was not configured."));
        }

        public Task<TranscriptionResult> TranscribeStreamingAsync(
            IStreamingTranscriptionProvider provider,
            TranscriptionRequest request,
            ChannelWriter<TranscriptChannelItem> writer,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TranslationResult> TranslateAsync(
            ITranslationProvider provider,
            TranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastTranslationRequest = request;
            return TranslateAsyncImpl?.Invoke(provider, request, cancellationToken)
                ?? Task.FromException<TranslationResult>(new InvalidOperationException("TranslateAsync was not configured."));
        }

        public Task<SingleSegmentTranslationTextResult> TranslateSingleSegmentTextAsync(
            ITranslationProvider provider,
            SingleSegmentTranslationTextRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TtsResult> GenerateSegmentTtsAsync(
            ITtsProvider provider,
            SingleSegmentTtsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DiarizationResult> DiarizeAsync(
            IDiarizationProvider provider,
            DiarizationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VocalSeparationResult> SeparateVocalsAsync(
            IVocalSeparationProvider provider,
            VocalSeparationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubTranscriptionProvider : ITranscriptionProvider
    {
        public Task<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubTranslationProvider : ITranslationProvider
    {
        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SingleSegmentTranslationTextResult> TranslateSingleSegmentTextAsync(
            SingleSegmentTranslationTextRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CaptureProgress<T>(Action<T> capture) : IProgress<T>
    {
        public void Report(T value) => capture(value);
    }
}
