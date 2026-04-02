using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

/// <summary>
/// Reads persisted transcript and translation artifacts through the strict
/// contract layer so coordinator code does not parse JsonElement trees directly.
/// </summary>
public sealed class SessionArtifactReader
{
    public Task<TranscriptArtifact> LoadTranscriptAsync(
        string transcriptPath,
        CancellationToken cancellationToken = default) =>
        ArtifactJson.LoadTranscriptAsync(transcriptPath, cancellationToken);

    public Task<TranslationArtifact> LoadTranslationAsync(
        string translationPath,
        CancellationToken cancellationToken = default) =>
        ArtifactJson.LoadTranslationAsync(translationPath, cancellationToken);

    public async Task<IReadOnlyList<WorkflowSegmentState>> BuildWorkflowSegmentsAsync(
        WorkflowSessionSnapshot session,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session.TranscriptPath) || !File.Exists(session.TranscriptPath))
            return [];

        var transcript = await LoadTranscriptAsync(session.TranscriptPath, cancellationToken);
        Dictionary<string, string>? translationTexts = null;
        Dictionary<string, string>? translationSpeakerIds = null;
        if (!string.IsNullOrWhiteSpace(session.TranslationPath) && File.Exists(session.TranslationPath))
        {
            var translation = await LoadTranslationAsync(session.TranslationPath, cancellationToken);
            translationTexts = new Dictionary<string, string>();
            translationSpeakerIds = new Dictionary<string, string>();
            foreach (var segment in translation.Segments ?? [])
            {
                if (!string.IsNullOrWhiteSpace(segment.Id) &&
                    !string.IsNullOrWhiteSpace(segment.TranslatedText))
                {
                    translationTexts[segment.Id] = segment.TranslatedText;
                }

                if (!string.IsNullOrWhiteSpace(segment.Id) &&
                    !string.IsNullOrWhiteSpace(segment.SpeakerId))
                {
                    translationSpeakerIds[segment.Id] = segment.SpeakerId;
                }
            }
        }

        var ttsSegmentPaths = session.TtsSegmentAudioPaths;
        var speakerVoiceAssignments = session.SpeakerVoiceAssignments;
        var speakerReferenceAudioPaths = session.SpeakerReferenceAudioPaths;
        var result = new List<WorkflowSegmentState>();
        foreach (var segment in transcript.Segments ?? [])
        {
            var id = SessionWorkflowCoordinator.SegmentId(segment.Start);
            string? translatedText = null;
            var hasTranslation = translationTexts != null
                && translationTexts.TryGetValue(id, out translatedText);
            var hasTtsAudio = ttsSegmentPaths != null
                && ttsSegmentPaths.TryGetValue(id, out var audioPath)
                && File.Exists(audioPath);
            var speakerId = ResolveSpeakerId(segment, id, translationSpeakerIds);
            var assignedVoice = ResolveAssignedVoice(speakerId, speakerVoiceAssignments);
            var hasReferenceAudio = HasReferenceAudio(speakerId, speakerReferenceAudioPaths);

            result.Add(new WorkflowSegmentState(
                id,
                segment.Start,
                segment.End,
                segment.Text ?? string.Empty,
                hasTranslation,
                translatedText,
                hasTtsAudio,
                speakerId,
                assignedVoice,
                hasReferenceAudio));
        }

        return result;
    }

    public async Task<string> GetTranslatedTextAsync(
        string translationPath,
        string segmentId,
        CancellationToken cancellationToken = default)
    {
        var translation = await LoadTranslationAsync(translationPath, cancellationToken);
        foreach (var segment in translation.Segments ?? [])
        {
            if (segment.Id == segmentId && !string.IsNullOrWhiteSpace(segment.TranslatedText))
                return segment.TranslatedText;
        }

        throw new InvalidOperationException($"Translated text not found for segment '{segmentId}'.");
    }

    public async Task<string> GetSourceTextAsync(
        string translationPath,
        string segmentId,
        CancellationToken cancellationToken = default)
    {
        var translation = await LoadTranslationAsync(translationPath, cancellationToken);
        foreach (var segment in translation.Segments ?? [])
        {
            if (segment.Id == segmentId && segment.Text is not null)
                return segment.Text;
        }

        throw new InvalidOperationException($"Source text not found for segment '{segmentId}'.");
    }

    private static string? ResolveSpeakerId(
        TranscriptSegmentArtifact segment,
        string segmentId,
        Dictionary<string, string>? translationSpeakerIds)
    {
        if (!string.IsNullOrWhiteSpace(segment.SpeakerId))
            return segment.SpeakerId;

        if (translationSpeakerIds is null)
            return null;

        return translationSpeakerIds.TryGetValue(segmentId, out var speakerId)
            ? speakerId
            : null;
    }

    private static string? ResolveAssignedVoice(
        string? speakerId,
        Dictionary<string, string>? speakerVoiceAssignments)
    {
        if (string.IsNullOrWhiteSpace(speakerId) || speakerVoiceAssignments is null)
            return null;

        return speakerVoiceAssignments.TryGetValue(speakerId, out var assignedVoice)
            ? assignedVoice
            : null;
    }

    private static bool HasReferenceAudio(
        string? speakerId,
        Dictionary<string, string>? speakerReferenceAudioPaths)
    {
        if (string.IsNullOrWhiteSpace(speakerId) || speakerReferenceAudioPaths is null)
            return false;

        return speakerReferenceAudioPaths.TryGetValue(speakerId, out var path)
            && !string.IsNullOrWhiteSpace(path)
            && File.Exists(path);
    }
}
