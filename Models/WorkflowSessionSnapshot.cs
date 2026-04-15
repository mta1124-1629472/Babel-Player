using System;
using System.Collections.Generic;

namespace Babel.Player.Models;

public sealed record WorkflowSessionSnapshot(
    Guid SessionId,
    SessionWorkflowStage Stage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastUpdatedAtUtc,
    string StatusMessage,
    string? SourceMediaPath = null,
    string? IngestedMediaPath = null,
    DateTimeOffset? MediaLoadedAtUtc = null,
    string? TranscriptPath = null,
    DateTimeOffset? TranscribedAtUtc = null,
    string? TranslationPath = null,
    string? SourceLanguage = null,
    string? TargetLanguage = null,
    DateTimeOffset? TranslatedAtUtc = null,
    string? TtsPath = null,
    string? TtsVoice = null,
    DateTimeOffset? TtsGeneratedAtUtc = null,
    string? TtsSegmentsPath = null,
    Dictionary<string, string>? TtsSegmentAudioPaths = null,
    /// <summary>
    /// Duration in seconds of each generated TTS audio clip, keyed by segment ID.
    /// Null or missing entries mean duration is unknown (older sessions or providers that don't report it).
    /// </summary>
    Dictionary<string, double>? TtsSegmentDurations = null,
    Dictionary<string, string>? SpeakerVoiceAssignments = null,
    Dictionary<string, string>? SpeakerReferenceAudioPaths = null,
    bool MultiSpeakerEnabled = true,
    string? DefaultTtsVoiceFallback = null,
    string? DiarizationProvider = null,
    DateTimeOffset? SpeakersDetectedAtUtc = null,
    InferenceRuntime? TranscriptionRuntime = null,
    string? TranscriptionProvider = null,
    string? TranscriptionModel = null,
    string? TranscriptionLanguageHint = null,
    InferenceRuntime? TranslationRuntime = null,
    string? TranslationProvider = null,
    string? TranslationModel = null,
    InferenceRuntime? TtsRuntime = null,
    string? TtsProvider = null,
    Dictionary<string, SegmentTimingMode>? SegmentTimingModeOverrides = null)
{
    public static WorkflowSessionSnapshot CreateNew(DateTimeOffset nowUtc)
    {
        return new WorkflowSessionSnapshot(
            Guid.NewGuid(),
            SessionWorkflowStage.Foundation,
            nowUtc,
            nowUtc,
            "Foundation ready. Media ingest, transcription, translation, and dubbing are not implemented yet.");
    }
}
