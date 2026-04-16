using Babel.Player.Models;

namespace Babel.Player.Services.Pipeline;

/// <summary>
/// Encodes allowed pipeline advance steps from <see cref="SessionWorkflowStage"/> and diarization settings.
/// </summary>
internal enum PipelineAdvanceAction
{
    Transcribe,
    Diarize,
    /// <summary>Streaming translation + TTS from an existing transcript (commits translation and dub).</summary>
    TranslateAndDubFromTranscript,
    /// <summary>Generate combined dub from translation artifact.</summary>
    GenerateTts,
}

internal static class PipelineStateMachine
{
    /// <summary>
    /// Full streaming ASR → translation → TTS path (skips separate transcribe/diarize steps in this invocation).
    /// </summary>
    internal static bool ShouldRunFullStreamingPipelineFirst(
        SessionWorkflowStage currentStage,
        bool shouldRunDiarization) =>
        currentStage < SessionWorkflowStage.Transcribed && !shouldRunDiarization;

    /// <summary>
    /// Next action after re-reading <see cref="SessionWorkflowCoordinator.CurrentSession"/>.Stage, or null when nothing left to advance.
    /// </summary>
    internal static PipelineAdvanceAction? GetNextAdvanceAction(
        SessionWorkflowStage currentStage,
        bool shouldRunDiarization)
    {
        if (currentStage < SessionWorkflowStage.Transcribed)
            return PipelineAdvanceAction.Transcribe;

        if (shouldRunDiarization && currentStage < SessionWorkflowStage.Diarized)
            return PipelineAdvanceAction.Diarize;

        if (currentStage < SessionWorkflowStage.Translated)
            return PipelineAdvanceAction.TranslateAndDubFromTranscript;

        if (currentStage < SessionWorkflowStage.TtsGenerated)
            return PipelineAdvanceAction.GenerateTts;

        return null;
    }

    /// <summary>
    /// Continuation after diarization: translation+dub or TTS-only.
    /// </summary>
    internal static PipelineAdvanceAction? GetContinuationActionAfterDiarized(SessionWorkflowStage currentStage)
    {
        if (currentStage < SessionWorkflowStage.Translated)
            return PipelineAdvanceAction.TranslateAndDubFromTranscript;

        if (currentStage < SessionWorkflowStage.TtsGenerated)
            return PipelineAdvanceAction.GenerateTts;

        return null;
    }
}
