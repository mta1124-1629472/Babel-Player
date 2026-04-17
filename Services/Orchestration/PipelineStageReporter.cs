using System;
using Babel.Player.Models;

namespace Babel.Player.Services.Orchestration;

internal static class PipelineStageReporter
{
    internal static string GetPipelineStageTitle(SessionWorkflowStage stage) =>
        stage switch
        {
            SessionWorkflowStage.Transcribed => "Transcription",
            SessionWorkflowStage.Diarized => "Speaker Mapping",
            SessionWorkflowStage.Translated => "Translation",
            SessionWorkflowStage.TtsGenerated => "Dub",
            _ => stage.ToString(),
        };

    internal static void ReportStage(
        PipelineStageContext? context,
        string detail,
        double progress01,
        bool isIndeterminate,
        string? streamingStatus = null)
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
                isIndeterminate,
                streamingStatus));
    }

    internal static IProgress<double>? CreateStageDownloadProgress(
        PipelineStageContext? context,
        IProgress<double>? rawProgress,
        string detailPrefix)
    {
        if (context is null && rawProgress is null)
            return null;

        return new InlineProgress<double>(value =>
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

    internal static string NormalizePipelineLanguage(string? raw, string nonNormalizedFallback)
    {
        var normalized = LanguageCode.NormalizeForPersistence(raw);
        if (normalized is not null)
            return normalized;
        if (string.IsNullOrWhiteSpace(raw))
            return nonNormalizedFallback;
        return raw.Trim();
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
