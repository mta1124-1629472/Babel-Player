using System;
using System.Collections.Generic;
using Babel.Player.Models;
using SharedOrchestration = Babel.Player.Services.Orchestration;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    internal sealed record PipelineStageUpdate(
        int StageIndex,
        int StageCount,
        SessionWorkflowStage TargetStage,
        string Title,
        string Detail,
        double Progress01,
        bool IsIndeterminate,
        string? StreamingStatus = null)
    {
        internal SharedOrchestration.PipelineStageUpdate ToShared() =>
            new(
                StageIndex,
                StageCount,
                TargetStage,
                Title,
                Detail,
                Progress01,
                IsIndeterminate,
                StreamingStatus);

        internal static PipelineStageUpdate FromShared(SharedOrchestration.PipelineStageUpdate update) =>
            new(
                update.StageIndex,
                update.StageCount,
                update.TargetStage,
                update.Title,
                update.Detail,
                update.Progress01,
                update.IsIndeterminate,
                update.StreamingStatus);
    }

    internal readonly record struct PipelineStageContext(
        int StageIndex,
        int StageCount,
        SessionWorkflowStage TargetStage,
        string Title,
        IProgress<PipelineStageUpdate>? Reporter)
    {
        internal SharedOrchestration.PipelineStageContext ToShared()
        {
            var reporterTarget = Reporter;
            IProgress<SharedOrchestration.PipelineStageUpdate>? reporter = reporterTarget is null
                ? null
                : new Progress<SharedOrchestration.PipelineStageUpdate>(
                    update => reporterTarget.Report(PipelineStageUpdate.FromShared(update)));
            return new SharedOrchestration.PipelineStageContext(
                StageIndex,
                StageCount,
                TargetStage,
                Title,
                reporter);
        }

        internal static PipelineStageContext? FromShared(SharedOrchestration.PipelineStageContext? context)
        {
            if (context is not { } shared)
                return null;

            IProgress<PipelineStageUpdate>? reporter = shared.Reporter is null
                ? null
                : new Progress<PipelineStageUpdate>(
                    update => shared.Reporter.Report(update.ToShared()));
            return new PipelineStageContext(
                shared.StageIndex,
                shared.StageCount,
                shared.TargetStage,
                shared.Title,
                reporter);
        }
    }

    private static IReadOnlyList<SessionWorkflowStage> GetAdvancePipelineStages(
        SessionWorkflowStage currentStage,
        bool shouldRunDiarization)
    {
        var stages = new List<SessionWorkflowStage>(capacity: shouldRunDiarization ? 4 : 3);
        if (currentStage < SessionWorkflowStage.Transcribed)
            stages.Add(SessionWorkflowStage.Transcribed);

        if (shouldRunDiarization && currentStage < SessionWorkflowStage.Diarized)
            stages.Add(SessionWorkflowStage.Diarized);

        if (currentStage < SessionWorkflowStage.Translated)
            stages.Add(SessionWorkflowStage.Translated);
        if (currentStage < SessionWorkflowStage.TtsGenerated)
            stages.Add(SessionWorkflowStage.TtsGenerated);
        return stages;
    }

    private static IReadOnlyList<SessionWorkflowStage> GetContinuationPipelineStages(SessionWorkflowStage currentStage)
    {
        var stages = new List<SessionWorkflowStage>(capacity: 2);
        if (currentStage < SessionWorkflowStage.Translated)
            stages.Add(SessionWorkflowStage.Translated);
        if (currentStage < SessionWorkflowStage.TtsGenerated)
            stages.Add(SessionWorkflowStage.TtsGenerated);
        return stages;
    }

    private static PipelineStageContext? GetStageContext(
        IReadOnlyList<SessionWorkflowStage> remainingStages,
        SessionWorkflowStage targetStage,
        IProgress<PipelineStageUpdate>? stageProgress)
    {
        if (stageProgress is null)
            return null;

        var stageIndex = -1;
        for (var i = 0; i < remainingStages.Count; i++)
        {
            if (remainingStages[i] == targetStage)
            {
                stageIndex = i;
                break;
            }
        }
        if (stageIndex < 0)
            return null;

        return new PipelineStageContext(
            stageIndex + 1,
            remainingStages.Count,
            targetStage,
            GetPipelineStageTitle(targetStage),
            stageProgress);
    }

    private static string GetPipelineStageTitle(SessionWorkflowStage stage) =>
        SharedOrchestration.PipelineStageReporter.GetPipelineStageTitle(stage);

    private static void ReportStage(
        PipelineStageContext? context,
        string detail,
        double progress01,
        bool isIndeterminate,
        string? streamingStatus = null) =>
        SharedOrchestration.PipelineStageReporter.ReportStage(
            context is { } stageContext ? stageContext.ToShared() : null,
            detail,
            progress01,
            isIndeterminate,
            streamingStatus);

    private static IProgress<double>? CreateStageDownloadProgress(
        PipelineStageContext? context,
        IProgress<double>? rawProgress,
        string detailPrefix) =>
        SharedOrchestration.PipelineStageReporter.CreateStageDownloadProgress(
            context is { } stageContext ? stageContext.ToShared() : null,
            rawProgress,
            detailPrefix);
}
