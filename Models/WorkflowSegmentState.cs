using System;

namespace Babel.Player.Models;

public sealed record WorkflowSegmentState(
    string SegmentId,
    double StartSeconds,
    double EndSeconds,
    string SourceText,
    bool HasTranslation,
    string? TranslatedText,
    bool HasTtsAudio,
    string? SpeakerId = null,
    string? AssignedVoice = null,
    bool HasReferenceAudio = false);
