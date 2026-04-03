using System;
using System.Collections.Generic;
using Babel.Player.Models;

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
        bool IsIndeterminate);

    internal readonly record struct PipelineStageContext(
        int StageIndex,
        int StageCount,
        SessionWorkflowStage TargetStage,
        string Title,
        IProgress<PipelineStageUpdate>? Reporter);

    private static IReadOnlyList<SessionWorkflowStage> GetRemainingPipelineStages(SessionWorkflowStage currentStage)
    {
        var stages = new List<SessionWorkflowStage>(capacity: 3);
        if (currentStage < SessionWorkflowStage.Transcribed)
            stages.Add(SessionWorkflowStage.Transcribed);
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
        stage switch
        {
            SessionWorkflowStage.Transcribed => "Transcription",
            SessionWorkflowStage.Translated => "Translation",
            SessionWorkflowStage.TtsGenerated => "Dub",
            _ => stage.ToString(),
        };

    private static void ReportStage(
        PipelineStageContext? context,
        string detail,
        double progress01,
        bool isIndeterminate)
    {
        if (context is not { } stageContext || stageContext.Reporter is null)
            return;

        var clampedProgress = double.IsFinite(progress01)
            ? Math.Clamp(progress01, 0d, 1d)
            : 0d;
        stageContext.Reporter.Report(
            new PipelineStageUpdate(
                stageContext.StageIndex,
                stageContext.StageCount,
                stageContext.TargetStage,
                stageContext.Title,
                detail,
                clampedProgress,
                isIndeterminate));
    }

    private static IProgress<double>? CreateStageDownloadProgress(
        PipelineStageContext? context,
        IProgress<double>? rawProgress,
        string detailPrefix)
    {
        if (context is null && rawProgress is null)
            return null;

        return new Progress<double>(value =>
        {
            var clampedProgress = double.IsFinite(value)
                ? Math.Clamp(value, 0d, 1d)
                : 0d;
            rawProgress?.Report(clampedProgress);
            if (context is { } stageContext)
            {
                ReportStage(
                    stageContext,
                    $"{detailPrefix} ({clampedProgress:P0}).",
                    clampedProgress,
                    isIndeterminate: false);
            }
        });
    }
}
