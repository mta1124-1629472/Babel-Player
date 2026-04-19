using System;
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

    /// <summary>
    /// Validates that the artifact files referenced by a session snapshot exist and, if not, downgrades the snapshot's stage and clears downstream artifacts.
    /// </summary>
    /// <param name="snapshot">The session snapshot to validate; its runtime/provider provenance will be normalized as part of validation.</param>
    /// <returns>
    /// A ValidationResult containing:
    /// - the snapshot with normalized provenance and an updated Stage (downgraded if artifacts were missing),
    /// - the original stage prior to any downgrades,
    /// - a list of artifact keys that were cleared due to missing files (for example: "tts", "translation", "transcription", "media").
    /// </returns>
    public static ValidationResult ValidateArtifacts(WorkflowSessionSnapshot snapshot)
    {
        snapshot = NormalizeRuntimeProvenance(snapshot);
        var stage = snapshot.Stage;
        var originalStage = stage;
        var cleared = new List<string>();

        if (stage >= SessionWorkflowStage.TtsGenerated
            && !ArtifactIntegrityValidator.ValidateTts(snapshot, out _))
        {
            stage = SessionWorkflowStage.Translated;
            snapshot = ClearTtsOutputs(snapshot);
            cleared.Add("tts");
        }

        if (stage >= SessionWorkflowStage.Translated
            && !ArtifactIntegrityValidator.ValidateTranslation(snapshot, out _))
        {
            stage = HasDiarizationMarker(snapshot)
                ? SessionWorkflowStage.Diarized
                : SessionWorkflowStage.Transcribed;
            snapshot = ClearTranslationOutputs(snapshot);
            cleared.Add("translation");
        }

        if (stage == SessionWorkflowStage.Diarized && !HasDiarizationMarker(snapshot))
        {
            stage = SessionWorkflowStage.Transcribed;
            snapshot = ClearDiarizationOutputs(snapshot);
            cleared.Add("diarization");
        }

        if (stage >= SessionWorkflowStage.Transcribed)
        {
            if (!ArtifactIntegrityValidator.ValidateStemPair(snapshot, out _))
            {
                stage = SessionWorkflowStage.MediaLoaded;
                var hadDiarization = HasDiarizationMarker(snapshot);
                snapshot = ClearTranscriptionOutputs(snapshot);
                cleared.Add("vocal_separation");
                if (hadDiarization)
                    cleared.Add("diarization");
                cleared.Add("transcription");
            }
            else if (!ArtifactIntegrityValidator.ValidateTranscript(snapshot, out _))
            {
                stage = SessionWorkflowStage.MediaLoaded;
                var hadStems = !string.IsNullOrWhiteSpace(snapshot.VocalsAudioPath);
                var hadDiarization = HasDiarizationMarker(snapshot);
                snapshot = ClearTranscriptionOutputs(snapshot);
                if (hadStems)
                    cleared.Add("vocal_separation");
                if (hadDiarization)
                    cleared.Add("diarization");
                cleared.Add("transcription");
            }
        }

        if (!ArtifactIntegrityValidator.ValidateStemPair(snapshot, out _))
        {
            var hadDiarization = HasDiarizationMarker(snapshot);
            snapshot = ClearTranscriptionOutputs(snapshot);
            cleared.Add("vocal_separation");
            if (hadDiarization)
                cleared.Add("diarization");
            if (stage > SessionWorkflowStage.MediaLoaded) stage = SessionWorkflowStage.MediaLoaded;
        }

        if (stage >= SessionWorkflowStage.MediaLoaded
            && !ArtifactIntegrityValidator.ValidateMedia(snapshot, out _))
        {
            // Ingested media is missing - downgrade to Foundation stage
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
        snapshot = NormalizeRuntimeProvenance(snapshot);

        bool transcriptionChanged = snapshot.TranscriptionRuntime != settings.TranscriptionRuntime
            || snapshot.TranscriptionProvider != settings.TranscriptionProvider
            || snapshot.TranscriptionModel != settings.TranscriptionModel
            || !TranscriptionLanguageHintsMatch(snapshot.TranscriptionLanguageHint, settings.TranscriptionLanguageHint);
        var hadSeparatedVocals = !string.IsNullOrWhiteSpace(snapshot.VocalsAudioPath);
        if (hadSeparatedVocals != settings.VocalSeparationEnabled)
            transcriptionChanged = true;
        bool translationChanged = snapshot.TranslationRuntime != settings.TranslationRuntime
            || snapshot.TranslationProvider != settings.TranslationProvider
            || snapshot.TranslationModel != settings.TranslationModel
            || !LanguageCode.TargetLanguagesMatch(snapshot.TargetLanguage, settings.TargetLanguage);
        bool ttsChanged = snapshot.TtsRuntime != settings.TtsRuntime
            || snapshot.TtsProvider != settings.TtsProvider
            || snapshot.TtsVoice != settings.TtsVoice
            || snapshot.DubTimingMode != settings.DubTimingMode
            || snapshot.AmbianceMixDb != settings.AmbianceMixDb;

        var effectiveStage = ResolveArtifactStage(snapshot);
        return effectiveStage switch
        {
            SessionWorkflowStage.Foundation => PipelineInvalidation.None,
            SessionWorkflowStage.MediaLoaded => PipelineInvalidation.None,
            SessionWorkflowStage.Transcribed => transcriptionChanged ? PipelineInvalidation.Transcription : PipelineInvalidation.None,
            SessionWorkflowStage.Diarized => transcriptionChanged ? PipelineInvalidation.Transcription : PipelineInvalidation.None,
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
            && ArtifactIntegrityValidator.ValidateTts(snapshot, out _))
            return SessionWorkflowStage.TtsGenerated;

        if (snapshot.Stage >= SessionWorkflowStage.Translated
            && ArtifactIntegrityValidator.ValidateTranslation(snapshot, out _))
            return SessionWorkflowStage.Translated;

        if (snapshot.Stage >= SessionWorkflowStage.Diarized
            && ArtifactIntegrityValidator.ValidateTranscript(snapshot, out _)
            && HasDiarizationMarker(snapshot))
            return SessionWorkflowStage.Diarized;

        if (snapshot.Stage >= SessionWorkflowStage.Transcribed
            && ArtifactIntegrityValidator.ValidateTranscript(snapshot, out _))
            return SessionWorkflowStage.Transcribed;

        if (snapshot.Stage >= SessionWorkflowStage.MediaLoaded
            && ArtifactIntegrityValidator.ValidateMedia(snapshot, out _))
            return SessionWorkflowStage.MediaLoaded;

        return SessionWorkflowStage.Foundation;
    }

    public static string? NormalizeTranscriptionLanguageHint(string? value) =>
        LanguageCode.NormalizeForPersistence(value);

    public static bool TranscriptionLanguageHintsMatch(string? a, string? b) =>
        LanguageCode.LanguageEquals(a, b);

    public static string DescribeSessionProvenance(WorkflowSessionSnapshot snapshot) =>
        $"stage={snapshot.Stage}, " +
        $"txc={snapshot.TranscriptionRuntime?.ToString() ?? "<null>"}/{snapshot.TranscriptionProvider ?? "<null>"}/{snapshot.TranscriptionModel ?? "<null>"}/asrHint={snapshot.TranscriptionLanguageHint ?? "<auto>"}, " +
        $"vox={(string.IsNullOrWhiteSpace(snapshot.VocalsAudioPath) ? "off" : "on")}, " +
        $"trn={snapshot.TranslationRuntime?.ToString() ?? "<null>"}/{snapshot.TranslationProvider ?? "<null>"}/{snapshot.TranslationModel ?? "<null>"}, " +
        $"tts={snapshot.TtsRuntime?.ToString() ?? "<null>"}/{snapshot.TtsProvider ?? "<null>"}/{snapshot.TtsVoice ?? "<null>"}, " +
        $"srcLang={snapshot.SourceLanguage ?? "<null>"}, tgtLang={snapshot.TargetLanguage ?? "<null>"}";

    public static WorkflowSessionSnapshot NormalizeRuntimeProvenance(WorkflowSessionSnapshot snapshot) =>
        snapshot with
        {
            TranscriptionRuntime = ResolveRuntime(snapshot.TranscriptionRuntime, snapshot.TranscriptionProvider, InferenceRuntimeCatalog.InferTranscriptionRuntime),
            TranslationRuntime = ResolveRuntime(snapshot.TranslationRuntime, snapshot.TranslationProvider, InferenceRuntimeCatalog.InferTranslationRuntime),
            TtsRuntime = ResolveRuntime(snapshot.TtsRuntime, snapshot.TtsProvider, InferenceRuntimeCatalog.InferTtsRuntime),
            TranscriptionProvider = NormalizeStageProvider(
                ResolveRuntime(snapshot.TranscriptionRuntime, snapshot.TranscriptionProvider, InferenceRuntimeCatalog.InferTranscriptionRuntime),
                snapshot.TranscriptionProvider,
                InferenceRuntimeCatalog.NormalizeTranscriptionProvider),
            TranslationProvider = NormalizeStageProvider(
                ResolveRuntime(snapshot.TranslationRuntime, snapshot.TranslationProvider, InferenceRuntimeCatalog.InferTranslationRuntime),
                snapshot.TranslationProvider,
                InferenceRuntimeCatalog.NormalizeTranslationProvider),
            TtsProvider = NormalizeStageProvider(
                ResolveRuntime(snapshot.TtsRuntime, snapshot.TtsProvider, InferenceRuntimeCatalog.InferTtsRuntime),
                snapshot.TtsProvider,
                InferenceRuntimeCatalog.NormalizeTtsProvider),
        };

    public static WorkflowSessionSnapshot ClearTtsOutputs(WorkflowSessionSnapshot snapshot) =>
        snapshot with
        {
            TtsPath = null,
            MixedDubAudioPath = null,
            TtsVoice = null,
            TtsGeneratedAtUtc = null,
            TtsSegmentsPath = null,
            TtsSegmentAudioPaths = null,
            TtsSegmentDurations = null,
            TtsRuntime = null,
            TtsProvider = null,
            TtsSettingsDriftedSinceArtifact = false,
            TtsRunId = null,
            DubTimingMode = null,
            AmbianceMixDb = null,
        };

    public static WorkflowSessionSnapshot ClearTranslationOutputs(WorkflowSessionSnapshot snapshot) =>
        ClearTtsOutputs(snapshot) with
        {
            TranslationPath = null,
            TargetLanguage = null,
            TranslatedAtUtc = null,
            TranslationRuntime = null,
            TranslationProvider = null,
            TranslationModel = null,
            TranslationSettingsDriftedSinceArtifact = false,
            TranslationRunId = null,
        };

    public static WorkflowSessionSnapshot ClearDiarizationOutputs(WorkflowSessionSnapshot snapshot) =>
        ClearTranslationOutputs(snapshot) with
        {
            SpeakerVoiceAssignments = null,
            SpeakerReferenceAudioPaths = null,
            SegmentTimingModeOverrides = null,
            DefaultTtsVoiceFallback = null,
            DiarizationProvider = null,
            SpeakersDetectedAtUtc = null,
        };

    public static WorkflowSessionSnapshot ClearTranscriptionOutputs(WorkflowSessionSnapshot snapshot) =>
        ClearDiarizationOutputs(snapshot) with
        {
            TranscriptPath = null,
            SourceLanguage = null,
            TranscribedAtUtc = null,
            TranscriptionRuntime = null,
            TranscriptionProvider = null,
            TranscriptionModel = null,
            TranscriptionLanguageHint = null,
            VocalsAudioPath = null,
            AmbianceAudioPath = null,
        };

    public static WorkflowSessionSnapshot ClearMediaLoadedOutputs(WorkflowSessionSnapshot snapshot) =>
        ClearTranscriptionOutputs(snapshot) with
        {
            IngestedMediaPath = null,
            MediaLoadedAtUtc = null,
        };

    private static InferenceRuntime? ResolveRuntime(
        InferenceRuntime? runtime,
        string? providerId,
        System.Func<string?, InferenceRuntime> inferRuntime) =>
        string.IsNullOrWhiteSpace(providerId)
            ? null
            : runtime ?? inferRuntime(providerId);

    private static string? NormalizeStageProvider(
        InferenceRuntime? runtime,
        string? providerId,
        System.Func<InferenceRuntime, string?, string> normalizeProvider)
    {
        if (string.IsNullOrWhiteSpace(providerId) || runtime is null)
            return null;

        return normalizeProvider(runtime.Value, providerId);
    }

    internal static bool HasDiarizationMarker(WorkflowSessionSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.DiarizationProvider)
        && snapshot.SpeakersDetectedAtUtc.HasValue;
}
