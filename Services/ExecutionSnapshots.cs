using System;
using System.Collections.Generic;
using System.IO;
using Babel.Player.Models;
using Babel.Player.Services.Planning;

namespace Babel.Player.Services;

internal readonly record struct ArtifactIdentity(string Path, long Length, long LastWriteUtcTicks)
{
    public static ArtifactIdentity Capture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException($"Artifact not found: {fullPath}", fullPath);

        return new ArtifactIdentity(fullPath, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    public bool Matches(string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
            return false;

        var fullPath = System.IO.Path.GetFullPath(candidatePath);
        if (!string.Equals(Path, fullPath, StringComparison.OrdinalIgnoreCase))
            return false;

        var info = new FileInfo(fullPath);
        return info.Exists
            && info.Length == Length
            && info.LastWriteTimeUtc.Ticks == LastWriteUtcTicks;
    }
}

internal sealed record TranslationExecutionSnapshot(
    Guid RunId,
    Guid SessionId,
    long SettingsRevision,
    long SessionRevision,
    StageExecutionPlan Plan,
    ProviderLease<ITranslationProvider> ProviderLease,
    string Model,
    string SourceLanguage,
    string TargetLanguage,
    string TranscriptPath,
    ArtifactIdentity TranscriptIdentity,
    string TranslationPath,
    string WorkingTranslationPath)
{
    public ITranslationProvider Provider => ProviderLease.Provider;
}

internal sealed record TtsExecutionSnapshot(
    Guid RunId,
    Guid SessionId,
    long SettingsRevision,
    long SessionRevision,
    StageExecutionPlan Plan,
    ProviderLease<ITtsProvider> ProviderLease,
    string Voice,
    string? Language,
    string TranslationPath,
    ArtifactIdentity TranslationIdentity,
    string TtsPath,
    string SegmentsDir,
    string? SourceVideoPath,
    string? AmbianceAudioPath,
    SegmentTimingMode DefaultTimingMode,
    double AmbianceMixDb,
    IReadOnlyDictionary<string, SegmentTimingMode> SegmentTimingOverrides,
    IReadOnlyDictionary<string, string> SpeakerVoiceAssignments,
    IReadOnlyDictionary<string, string> SpeakerReferenceAudioPaths,
    string? DefaultVoiceFallback)
{
    public ITtsProvider Provider => ProviderLease.Provider;

    public bool IsQwen =>
        string.Equals(Plan.ProviderId, ProviderNames.Qwen, StringComparison.Ordinal);

    public int MaxConcurrency => Math.Max(1, Provider.MaxConcurrency);

    public string ResolveVoiceForSegment(TranslationSegmentArtifact segment)
    {
        var speakerId = segment.SpeakerId;
        if (!string.IsNullOrWhiteSpace(speakerId)
            && SpeakerVoiceAssignments.TryGetValue(speakerId, out var mappedVoice)
            && !string.IsNullOrWhiteSpace(mappedVoice))
        {
            return mappedVoice;
        }

        return Voice;
    }

    public string? ResolveReferenceAudioForSegment(TranslationSegmentArtifact segment)
    {
        var speakerId = segment.SpeakerId;
        if (!string.IsNullOrWhiteSpace(speakerId)
            && SpeakerReferenceAudioPaths.TryGetValue(speakerId, out var speakerPath)
            && !string.IsNullOrWhiteSpace(speakerPath))
        {
            return speakerPath;
        }

        return SpeakerReferenceAudioPaths.TryGetValue(QwenReferenceKeys.SingleSpeakerDefault, out var defaultPath)
               && !string.IsNullOrWhiteSpace(defaultPath)
            ? defaultPath
            : null;
    }

    public SegmentTimingMode ResolveRenderTimingMode(string segmentId)
    {
        if (SegmentTimingOverrides.TryGetValue(segmentId, out var overrideMode))
        {
            if (overrideMode == SegmentTimingMode.Pause)
                return DubTimingDefaults.NormalizeRenderTimingMode(DefaultTimingMode);

            return DubTimingDefaults.NormalizeRenderTimingMode(overrideMode);
        }

        return DubTimingDefaults.NormalizeRenderTimingMode(DefaultTimingMode);
    }
}
