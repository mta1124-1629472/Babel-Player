using System;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Orchestration;
using Babel.Player.Services.Planning;
using Babel.Player.Services.Settings;
using SharedOrchestration = Babel.Player.Services.Orchestration;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator :
    ISessionStateAccessor,
    IStageExecutionPlanner,
    IProviderLifecycleManager,
    ISessionCommitter,
    IDiarizationExecutor
{
    WorkflowSessionSnapshot ISessionStateAccessor.CurrentSession
    {
        get => CurrentSession;
        set => CurrentSession = value;
    }

    AppSettings ISessionStateAccessor.CurrentSettings => CurrentSettings;

    HardwareSnapshot ISessionStateAccessor.HardwareSnapshot => HardwareSnapshot;

    string ISessionStateAccessor.GetSessionDirectory() => GetSessionDirectory();

    StageExecutionPlan IStageExecutionPlanner.ResolveAndApplyExecutionPlan(InferenceStage stage) =>
        ResolveAndApplyExecutionPlan(stage);

    ITranscriptionProvider? IProviderLifecycleManager.TranscriptionService
    {
        get => _transcriptionService;
        set => _transcriptionService = value;
    }

    ITranscriptionProvider IProviderLifecycleManager.CreateTranscriptionService() =>
        CreateTranscriptionService();

    Task IProviderLifecycleManager.EnsureTranscriptionProviderReadyAsync(
        IProgress<double>? progress,
        SharedOrchestration.PipelineStageContext? stageContext,
        CancellationToken cancellationToken) =>
        EnsureTranscriptionProviderReadyAsync(
            progress,
            PipelineStageContext.FromShared(stageContext),
            cancellationToken);

    Task<TranslationExecutionSnapshot> IProviderLifecycleManager.PrepareTranslationExecutionSnapshotAsync(
        StageExecutionPlan stagePlan,
        string transcriptPath,
        string normalizedSourceLanguage,
        string normalizedTargetLanguage,
        IProgress<double>? progress,
        CancellationToken cancellationToken) =>
        PrepareTranslationExecutionSnapshotAsync(
            stagePlan,
            transcriptPath,
            normalizedSourceLanguage,
            normalizedTargetLanguage,
            progress,
            cancellationToken);

    Task<string> IProviderLifecycleManager.SeparateVocalsAsync(
        IProgress<double>? progress,
        SharedOrchestration.PipelineStageContext? stageContext,
        CancellationToken cancellationToken) =>
        SeparateVocalsAsync(
            progress,
            PipelineStageContext.FromShared(stageContext),
            cancellationToken);

    Task ISessionCommitter.CommitTranscriptionSessionStateAsync(
        TranscriptionResult result,
        string transcriptPath) =>
        CommitTranscriptionSessionStateAsync(result, transcriptPath);

    Task ISessionCommitter.CommitTranslationSessionStateAsync(
        TranslationExecutionSnapshot snapshot,
        TranslationResult result) =>
        CommitTranslationSessionStateAsync(snapshot, result);

    async Task<(bool SpeakerAssignmentsChanged, int SpeakerCount, int SegmentCount)>
        IDiarizationExecutor.ExecuteDiarizationAsync(
            string audioPath,
            string transcriptPath,
            CancellationToken ct,
            SessionWorkflowStage? resultingStage,
            string? statusMessage)
    {
        var outcome = await ExecuteDiarizationAsync(
                audioPath,
                transcriptPath,
                ct,
                resultingStage,
                statusMessage)
            .ConfigureAwait(false);
        return (outcome.SpeakerAssignmentsChanged, outcome.SpeakerCount, outcome.SegmentCount);
    }
}
