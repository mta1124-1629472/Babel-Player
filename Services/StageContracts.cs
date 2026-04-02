using System.Collections.Generic;

namespace Babel.Player.Services;

// --- TRANSCRIPTION ---
public sealed record TranscriptionRequest(
    string SourceAudioPath,
    string OutputJsonPath,
    string ModelName,
    string? LanguageHint = null,
    string CpuComputeType = "int8",
    int CpuThreads = 0,
    int NumWorkers = 1);

public sealed record TranscriptionResult(
    bool Success,
    IReadOnlyList<TranscriptSegment> Segments,
    string Language,
    double LanguageProbability,
    string? ErrorMessage);

public sealed record TranscriptSegment(
    double StartSeconds,
    double EndSeconds,
    string Text,
    string? SpeakerId = null);

// --- TRANSLATION ---
public sealed record TranslationRequest(
    string TranscriptJsonPath, 
    string OutputJsonPath, 
    string SourceLanguage, 
    string TargetLanguage, 
    string ModelName);

public sealed record SingleSegmentTranslationRequest(
    string SourceText, 
    string SegmentId, 
    string TranslationJsonPath, 
    string OutputJsonPath, 
    string SourceLanguage, 
    string TargetLanguage, 
    string ModelName);

public sealed record TranslationResult(
    bool Success,
    IReadOnlyList<TranslatedSegment> Segments,
    string SourceLanguage,
    string TargetLanguage,
    string? ErrorMessage);

public sealed record TranslatedSegment(
    double StartSeconds,
    double EndSeconds,
    string Text,
    string TranslatedText,
    string? SpeakerId = null);

// --- TTS ---
public sealed record TtsRequest(
    string TranslationJsonPath, 
    string OutputAudioPath, 
    string VoiceName);

public sealed record SingleSegmentTtsRequest(
    string Text, 
    string OutputAudioPath, 
    string VoiceName);

public sealed record TtsResult(
    bool Success,
    string AudioPath,
    string Voice,
    long FileSizeBytes,
    string? ErrorMessage);
