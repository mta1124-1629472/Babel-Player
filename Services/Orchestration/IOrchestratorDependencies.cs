using System;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Planning;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services.Orchestration;

/// Read/write access to session state and settings.
public interface ISessionStateAccessor
{
    WorkflowSessionSnapshot CurrentSession { get; set; }
    AppSettings CurrentSettings { get; }
    HardwareSnapshot HardwareSnapshot { get; }
    string GetSessionDirectory();
    void SaveCurrentSession();
}

/// Execution planning for pipeline stages.
public interface IStageExecutionPlanner
{
    StageExecutionPlan ResolveAndApplyExecutionPlan(InferenceStage stage);
}

/// Provider lifecycle: create, cache, and ensure readiness.
public interface IProviderLifecycleManager
{
    ITranscriptionProvider? TranscriptionService { get; set; }
    ITranslationProvider? TranslationService { get; set; }
    ITranscriptionProvider CreateTranscriptionService();
    ITranslationProvider CreateTranslationService();
    Task EnsureTranscriptionProviderReadyAsync(
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken);
    Task EnsureTranslationExecutionReadyAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken);
    Task<string> SeparateVocalsAsync(
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken);
}

/// Commits pipeline stage results to session state.
public interface ISessionCommitter
{
    void CommitTranscriptionSessionState(TranscriptionResult result, string transcriptPath);
    void CommitTranslationSessionState(
        TranslationResult result,
        string translationPath,
        string sourceLanguage,
        string targetLanguage);
}

/// Diarization execution.
public interface IDiarizationExecutor
{
    Task<(bool SpeakerAssignmentsChanged, int SpeakerCount, int SegmentCount)> ExecuteDiarizationAsync(
        string audioPath,
        string transcriptPath,
        CancellationToken ct,
        SessionWorkflowStage? resultingStage = null,
        string? statusMessage = null);
}
