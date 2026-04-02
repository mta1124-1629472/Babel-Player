using System.Collections.Generic;
using System.IO;
using Babel.Player.Models;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Centralizes session snapshot artifact/provenance semantics so the coordinator
/// can reason about workflow state without carrying all snapshot-shape details inline.
/// </summary>
public static class SessionSnapshotSemantics
{
    public sealed record ValidationResult(
        WorkflowSessionSnapshot Snapshot,
        SessionWorkflowStage OriginalStage,
        IReadOnlyList<string> ClearedArtifacts);

    public static ValidationResult ValidateArtifacts(WorkflowSessionSnapshot snapshot)
    {
        var stage = snapshot.Stage;
        var originalStage = stage;
        var cleared = new List<string>();

        if (stage >= SessionWorkflowStage.TtsGenerated
            && (string.IsNullOrEmpty(snapshot.TtsPath) || !File.Exists(snapshot.TtsPath)))
        {
            stage = SessionWorkflowStage.Translated;
            snapshot = ClearTtsOutputs(snapshot);
            cleared.Add("tts");
        }

        if (stage >= SessionWorkflowStage.Translated
            && (string.IsNullOrEmpty(snapshot.TranslationPath) || !File.Exists(snapshot.TranslationPath)))
        {
            stage = SessionWorkflowStage.Transcribed;
            snapshot = ClearTranslationOutputs(snapshot);
            cleared.Add("translation");
        }

        if (stage >= SessionWorkflowStage.Transcribed
            && (string.IsNullOrEmpty(snapshot.TranscriptPath) || !File.Exists(snapshot.TranscriptPath)))
        {
            stage = SessionWorkflowStage.MediaLoaded;
            snapshot = ClearTranscriptionOutputs(snapshot);
            cleared.Add("transcription");
        }

        if (stage >= SessionWorkflowStage.MediaLoaded
            && (string.IsNullOrEmpty(snapshot.IngestedMediaPath) || !File.Exists(snapshot.IngestedMediaPath)))
        {
            stage = SessionWorkflowStage.Foundation;
            snapshot = ClearMediaLoadedOutputs(snapshot);
            cleared.Add("media");
        }

        return new ValidationResult(
            snapshot with { Stage = stage },
            originalStage,
            cleared);
    }

    public static PipelineInvalidation ComputeInvalidation(
        WorkflowSessionSnapshot snapshot,
        AppSettings settings)
    {
        bool transcriptionChanged = snapshot.TranscriptionProvider != settings.TranscriptionProvider
            || snapshot.TranscriptionModel != settings.TranscriptionModel;
        bool translationChanged = snapshot.TranslationProvider != settings.TranslationProvider
            || snapshot.TranslationModel != settings.TranslationModel
            || snapshot.TargetLanguage != settings.TargetLanguage;
        bool ttsChanged = snapshot.TtsProvider != settings.TtsProvider
            || snapshot.TtsVoice != settings.TtsVoice;

        var effectiveStage = ResolveArtifactStage(snapshot);
        return effectiveStage switch
        {
            SessionWorkflowStage.Foundation => PipelineInvalidation.None,
            SessionWorkflowStage.MediaLoaded => PipelineInvalidation.None,
            SessionWorkflowStage.Transcribed => transcriptionChanged ? PipelineInvalidation.Transcription : PipelineInvalidation.None,
            SessionWorkflowStage.Translated => transcriptionChanged
                ? PipelineInvalidation.Transcription
                : translationChanged
                    ? PipelineInvalidation.Translation
                    : PipelineInvalidation.None,
            SessionWorkflowStage.TtsGenerated => transcriptionChanged
                ? PipelineInvalidation.Transcription
                : translationChanged
                    ? PipelineInvalidation.Translation
                    : ttsChanged
                        ? PipelineInvalidation.Tts
                        : PipelineInvalidation.None,
            _ => PipelineInvalidation.None,
        };
    }

    public static SessionWorkflowStage ResolveArtifactStage(WorkflowSessionSnapshot snapshot)
    {
        if (snapshot.Stage >= SessionWorkflowStage.TtsGenerated
            && !string.IsNullOrWhiteSpace(snapshot.TtsPath)
            && File.Exists(snapshot.TtsPath))
            return SessionWorkflowStage.TtsGenerated;

        if (snapshot.Stage >= SessionWorkflowStage.Translated
            && !string.IsNullOrWhiteSpace(snapshot.TranslationPath)
            && File.Exists(snapshot.TranslationPath))
            return SessionWorkflowStage.Translated;

        if (snapshot.Stage >= SessionWorkflowStage.Transcribed
            && !string.IsNullOrWhiteSpace(snapshot.TranscriptPath)
            && File.Exists(snapshot.TranscriptPath))
            return SessionWorkflowStage.Transcribed;

        if (snapshot.Stage >= SessionWorkflowStage.MediaLoaded
            && !string.IsNullOrWhiteSpace(snapshot.IngestedMediaPath)
            && File.Exists(snapshot.IngestedMediaPath))
            return SessionWorkflowStage.MediaLoaded;

        return SessionWorkflowStage.Foundation;
    }

    public static string DescribeSessionProvenance(WorkflowSessionSnapshot snapshot) =>
        $"stage={snapshot.Stage}, " +
        $"txc={snapshot.TranscriptionProvider ?? "<null>"}/{snapshot.TranscriptionModel ?? "<null>"}, " +
        $"trn={snapshot.TranslationProvider ?? "<null>"}/{snapshot.TranslationModel ?? "<null>"}, " +
        $"tts={snapshot.TtsProvider ?? "<null>"}/{snapshot.TtsVoice ?? "<null>"}, " +
        $"srcLang={snapshot.SourceLanguage ?? "<null>"}, tgtLang={snapshot.TargetLanguage ?? "<null>"}";

    public static WorkflowSessionSnapshot ClearTtsOutputs(WorkflowSessionSnapshot snapshot) =>
        snapshot with
        {
            TtsPath = null,
            TtsVoice = null,
            TtsGeneratedAtUtc = null,
            TtsSegmentsPath = null,
            TtsSegmentAudioPaths = null,
            TtsProvider = null,
        };

    public static WorkflowSessionSnapshot ClearTranslationOutputs(WorkflowSessionSnapshot snapshot) =>
        ClearTtsOutputs(snapshot) with
        {
            TranslationPath = null,
            TargetLanguage = null,
            TranslatedAtUtc = null,
            TranslationProvider = null,
            TranslationModel = null,
        };

    public static WorkflowSessionSnapshot ClearTranscriptionOutputs(WorkflowSessionSnapshot snapshot) =>
        ClearTranslationOutputs(snapshot) with
        {
            TranscriptPath = null,
            SourceLanguage = null,
            TranscribedAtUtc = null,
            TranscriptionProvider = null,
            TranscriptionModel = null,
        };

    public static WorkflowSessionSnapshot ClearMediaLoadedOutputs(WorkflowSessionSnapshot snapshot) =>
        ClearTranscriptionOutputs(snapshot) with
        {
            IngestedMediaPath = null,
            MediaLoadedAtUtc = null,
        };
}
