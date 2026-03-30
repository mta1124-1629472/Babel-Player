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
    Dictionary<string, string>? TtsSegmentAudioPaths = null)
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
