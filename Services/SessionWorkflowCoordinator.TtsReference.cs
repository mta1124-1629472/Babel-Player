using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{

    private async Task EnsureSingleSpeakerQwenReferenceClipAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(CurrentSettings.TtsProvider, ProviderNames.Qwen, StringComparison.Ordinal))
            return;

        if (CurrentSession.MultiSpeakerEnabled)
        {
            _log.Info("Qwen multi-speaker mode enabled; auto-bootstrap single-speaker reference is skipped.");
            return;
        }

        var references = CurrentSession.SpeakerReferenceAudioPaths;
        if (references is not null
            && references.TryGetValue(QwenReferenceKeys.SingleSpeakerDefault, out var existingPath)
            && !string.IsNullOrWhiteSpace(existingPath)
            && File.Exists(existingPath))
        {
            return;
        }

        var mediaPath = !string.IsNullOrWhiteSpace(CurrentSession.IngestedMediaPath)
            ? CurrentSession.IngestedMediaPath!
            : CurrentSession.SourceMediaPath;
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            throw new FileNotFoundException("Cannot create Qwen reference audio: source media is unavailable.", mediaPath);

        var refsDir = Path.Combine(GetSessionDirectory(), "tts", "references");
        Directory.CreateDirectory(refsDir);
        var outputPath = Path.Combine(refsDir, "qwen-single-speaker-reference.wav");

        if (!File.Exists(outputPath))
        {
            if (_audioProcessingService is not null)
            {
                await _audioProcessingService.ExtractAudioClipAsync(
                    mediaPath,
                    outputPath,
                    startTimeSeconds: 0,
                    durationSeconds: 30,
                    cancellationToken);
            }
            else
            {
                _log.Warning("Audio processing service unavailable. Qwen auto reference extraction skipped.");
                return;
            }
        }


        var updatedRefs = references is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(references, StringComparer.Ordinal);
        updatedRefs[QwenReferenceKeys.SingleSpeakerDefault] = outputPath;

        CurrentSession = CurrentSession with { SpeakerReferenceAudioPaths = updatedRefs };
        SaveCurrentSession();
        _log.Info($"Prepared Qwen single-speaker reference clip: {outputPath}");
    }

    private async Task EnsureMultiSpeakerReferenceClipsAsync(CancellationToken cancellationToken = default)
    {
        var isQwen = string.Equals(CurrentSettings.TtsProvider, ProviderNames.Qwen, StringComparison.Ordinal);
        if (!isQwen)
            return;
        if (!CurrentSession.MultiSpeakerEnabled)
            return;
        if (string.IsNullOrWhiteSpace(CurrentSession.TranscriptPath))
            return;

        var mediaPath = !string.IsNullOrWhiteSpace(CurrentSession.IngestedMediaPath)
            ? CurrentSession.IngestedMediaPath!
            : CurrentSession.SourceMediaPath;
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            return;

        var artifact = await ArtifactJson.LoadTranscriptAsync(CurrentSession.TranscriptPath, cancellationToken);
        if (artifact.Segments is null || artifact.Segments.Count == 0)
            return;

        // Find the longest segment for each speaker
        var bestBySpeaker = new Dictionary<string, (double Start, double End)>(StringComparer.Ordinal);
        foreach (var seg in artifact.Segments)
        {
            if (string.IsNullOrWhiteSpace(seg.SpeakerId))
                continue;
            var dur = seg.End - seg.Start;
            if (!bestBySpeaker.TryGetValue(seg.SpeakerId, out var best) || dur > (best.End - best.Start))
                bestBySpeaker[seg.SpeakerId] = (seg.Start, seg.End);
        }
        if (bestBySpeaker.Count == 0)
            return;

        if (_audioProcessingService is null)
        {
            _log.Warning("Audio processing service unavailable — multi-speaker auto-reference extraction skipped.");
            return;
        }


        const string providerTag = "qwen";
        var refsDir = Path.Combine(GetSessionDirectory(), "tts", "references");
        Directory.CreateDirectory(refsDir);

        var existing = CurrentSession.SpeakerReferenceAudioPaths
                       ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var updated = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        var anyNew = false;

        foreach (var (speakerId, (segStart, segEnd)) in bestBySpeaker)
        {
            if (updated.TryGetValue(speakerId, out var ep) &&
                !string.IsNullOrWhiteSpace(ep) && File.Exists(ep))
                continue;

            var extractDuration = Math.Min(segEnd - segStart, 10.0);
            var safeSpeakerId = string.Join("_", speakerId.Split(Path.GetInvalidFileNameChars()));
            var outputPath = Path.Combine(refsDir, $"{providerTag}-ref-{safeSpeakerId}.wav");

            try
            {
                await _audioProcessingService.ExtractAudioClipAsync(
                    mediaPath,
                    outputPath,
                    segStart,
                    extractDuration,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warning($"Auto-reference extraction failed for speaker '{speakerId}': {ex.Message}");
                continue;
            }


            updated[speakerId] = outputPath;
            anyNew = true;
            _log.Info($"Auto-extracted {extractDuration:F1}s reference for speaker '{speakerId}': {outputPath}");
        }

        if (!anyNew)
            return;

        CurrentSession = CurrentSession with { SpeakerReferenceAudioPaths = updated };
        SaveCurrentSession();
        _log.Info($"Multi-speaker reference extraction complete: {bestBySpeaker.Count} speakers processed.");
    }
}