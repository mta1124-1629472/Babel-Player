using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    /// <summary>
    /// Rewrites every segment with <paramref name="fromSpeakerId"/> to <paramref name="toSpeakerId"/> in the transcript
    /// and translation (if present), then remaps per-speaker reference and voice dictionaries on the session.
    /// </summary>
    /// <summary>
    /// Relabels all diarized transcript (and translation) segments from one speaker ID to another and updates session speaker mappings.
    /// </summary>
    /// <param name="fromSpeakerId">The speaker ID to replace; leading/trailing whitespace is trimmed and must not be null or empty.</param>
    /// <param name="toSpeakerId">The target speaker ID to assign; leading/trailing whitespace is trimmed and must not be null or empty.</param>
    /// <param name="cancellationToken">Token to cancel I/O operations performed while loading or writing artifact files.</param>
    /// <returns>The number of transcript segments whose SpeakerId was changed from <c>fromSpeakerId</c> to <c>toSpeakerId</c>.</returns>
    /// <remarks>
    /// Entry requirements: a saved transcript file path must exist on the current session (<see cref="CurrentSession.TranscriptPath"/>); otherwise the method throws.
    /// On success the method persists changes to the transcript and translation files (if modified), updates the session's speaker voice and reference audio mappings atomically, and calls <see cref="SaveCurrentSession"/>.
    /// If <c>fromSpeakerId</c> and <c>toSpeakerId</c> are equal after trimming, the method performs no work and returns 0.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="fromSpeakerId"/> or <paramref name="toSpeakerId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no saved transcript file is available on the current session.</exception>
    public async Task<int> MergeDiarizedSpeakersAsync(
        string fromSpeakerId,
        string toSpeakerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromSpeakerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toSpeakerId);

        var from = fromSpeakerId.Trim();
        var to = toSpeakerId.Trim();
        if (string.Equals(from, to, StringComparison.Ordinal))
            return 0;

        if (string.IsNullOrWhiteSpace(CurrentSession.TranscriptPath) || !File.Exists(CurrentSession.TranscriptPath))
            throw new InvalidOperationException("Cannot merge speakers without a saved transcript file.");

        var transcript = await ArtifactJson
            .LoadTranscriptAsync(CurrentSession.TranscriptPath, cancellationToken)
            .ConfigureAwait(false);
        var transcriptChanged = 0;
        if (transcript.Segments is not null)
        {
            foreach (var seg in transcript.Segments)
            {
                if (string.Equals(seg.SpeakerId, from, StringComparison.Ordinal))
                {
                    seg.SpeakerId = to;
                    transcriptChanged++;
                }
            }
        }

        if (transcriptChanged > 0)
        {
            await File.WriteAllTextAsync(
                    CurrentSession.TranscriptPath,
                    ArtifactJson.SerializeTranscript(transcript),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(CurrentSession.TranslationPath) &&
            File.Exists(CurrentSession.TranslationPath))
        {
            var translation = await ArtifactJson
                .LoadTranslationAsync(CurrentSession.TranslationPath, cancellationToken)
                .ConfigureAwait(false);
            if (translation.Segments is { Count: > 0 })
            {
                var any = false;
                foreach (var seg in translation.Segments)
                {
                    if (!string.Equals(seg.SpeakerId, from, StringComparison.Ordinal))
                        continue;
                    seg.SpeakerId = to;
                    any = true;
                }

                if (any)
                {
                    await File.WriteAllTextAsync(
                            CurrentSession.TranslationPath,
                            ArtifactJson.SerializeTranslation(translation),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        lock (_sessionLock)
        {
            var voices = CurrentSession.SpeakerVoiceAssignments is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(CurrentSession.SpeakerVoiceAssignments, StringComparer.Ordinal);
            if (voices.TryGetValue(from, out var voicePath) && !string.IsNullOrWhiteSpace(voicePath))
            {
                if (!voices.ContainsKey(to))
                    voices[to] = voicePath;
                voices.Remove(from);
            }
            else
            {
                voices.Remove(from);
            }

            var refs = CurrentSession.SpeakerReferenceAudioPaths is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(CurrentSession.SpeakerReferenceAudioPaths, StringComparer.Ordinal);
            if (refs.TryGetValue(from, out var refPath) && !string.IsNullOrWhiteSpace(refPath))
            {
                if (!refs.ContainsKey(to))
                    refs[to] = refPath;
                refs.Remove(from);
            }
            else
            {
                refs.Remove(from);
            }

            CurrentSession = CurrentSession with
            {
                SpeakerVoiceAssignments = voices.Count == 0 ? null : voices,
                SpeakerReferenceAudioPaths = refs.Count == 0 ? null : refs,
            };
        }

        SaveCurrentSession();
        return transcriptChanged;
    }
}
