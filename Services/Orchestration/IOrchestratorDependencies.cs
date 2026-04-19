using System;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Planning;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services.Orchestration;

/// Read/write access to session state and settings.
internal interface ISessionStateAccessor
{
    WorkflowSessionSnapshot CurrentSession { get; set; }
    AppSettings CurrentSettings { get; }
    HardwareSnapshot HardwareSnapshot { get; }
    string GetSessionDirectory();
    void SaveCurrentSession();
}

/// Execution planning for pipeline stages.
internal interface IStageExecutionPlanner
{
    StageExecutionPlan ResolveAndApplyExecutionPlan(InferenceStage stage);
}

/// Provider lifecycle: create, cache, and ensure readiness.
internal interface IProviderLifecycleManager
{
    ITranscriptionProvider? TranscriptionService { get; set; }
    ITranscriptionProvider CreateTranscriptionService();
    Task EnsureTranscriptionProviderReadyAsync(
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken);
    Task<TranslationExecutionSnapshot> PrepareTranslationExecutionSnapshotAsync(
        StageExecutionPlan stagePlan,
        string transcriptPath,
        string normalizedSourceLanguage,
        string normalizedTargetLanguage,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
    Task<string> SeparateVocalsAsync(
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken);
}

/// Commits pipeline stage results to session state.
internal interface ISessionCommitter
{
    Task CommitTranscriptionSessionStateAsync(TranscriptionResult result, string transcriptPath);
    Task CommitTranslationSessionStateAsync(
        TranslationExecutionSnapshot snapshot,
        TranslationResult result);
}

/// Diarization execution.
internal interface IDiarizationExecutor
{
    Task<(bool SpeakerAssignmentsChanged, int SpeakerCount, int SegmentCount)> ExecuteDiarizationAsync(
        string audioPath,
        string transcriptPath,
        CancellationToken ct,
        SessionWorkflowStage? resultingStage = null,
        string? statusMessage = null);
}
