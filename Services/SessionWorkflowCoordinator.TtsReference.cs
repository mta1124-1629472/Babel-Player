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
    private async Task EnsureSingleSpeakerXttsReferenceClipAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(CurrentSettings.TtsProvider, ProviderNames.XttsContainer, StringComparison.Ordinal))
            return;

        if (CurrentSession.MultiSpeakerEnabled)
        {
            _log.Info("XTTS multi-speaker mode enabled; auto-bootstrap single-speaker reference is skipped.");
            return;
        }

        var references = CurrentSession.SpeakerReferenceAudioPaths;
        if (references is not null
            && references.TryGetValue(XttsReferenceKeys.SingleSpeakerDefault, out var existingPath)
            && !string.IsNullOrWhiteSpace(existingPath)
            && File.Exists(existingPath))
        {
            return;
        }

        var mediaPath = !string.IsNullOrWhiteSpace(CurrentSession.IngestedMediaPath)
            ? CurrentSession.IngestedMediaPath!
            : CurrentSession.SourceMediaPath;
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            throw new FileNotFoundException("Cannot create XTTS reference audio: source media is unavailable.", mediaPath);

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg not found. XTTS auto reference extraction requires ffmpeg.");
        var refsDir = Path.Combine(GetSessionDirectory(), "tts", "references");
        Directory.CreateDirectory(refsDir);
        var outputPath = Path.Combine(refsDir, "single-speaker-reference.wav");

        if (!File.Exists(outputPath))
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add("00:00:03.0");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(mediaPath);
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("12");
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add("24000");
            psi.ArgumentList.Add("-sample_fmt");
            psi.ArgumentList.Add("s16");
            psi.ArgumentList.Add(outputPath);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffmpeg for XTTS reference extraction.");

            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;
            if (proc.ExitCode != 0 || !File.Exists(outputPath))
                throw new InvalidOperationException($"XTTS reference extraction failed: {stderr}");
        }

        var updated = references is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(references, StringComparer.Ordinal);
        updated[XttsReferenceKeys.SingleSpeakerDefault] = outputPath;

        CurrentSession = CurrentSession with { SpeakerReferenceAudioPaths = updated };
        SaveCurrentSession();
        _log.Info($"Prepared XTTS single-speaker reference clip: {outputPath}");
    }

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

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg not found. Qwen auto reference extraction requires ffmpeg.");
        var refsDir = Path.Combine(GetSessionDirectory(), "tts", "references");
        Directory.CreateDirectory(refsDir);
        var outputPath = Path.Combine(refsDir, "qwen-single-speaker-reference.wav");

        if (!File.Exists(outputPath))
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(mediaPath);
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("30");
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add("16000");
            psi.ArgumentList.Add("-sample_fmt");
            psi.ArgumentList.Add("s16");
            psi.ArgumentList.Add(outputPath);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffmpeg for Qwen reference extraction.");

            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;
            if (proc.ExitCode != 0 || !File.Exists(outputPath))
                throw new InvalidOperationException($"Qwen reference extraction failed: {stderr}");
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
        var isXtts = string.Equals(CurrentSettings.TtsProvider, ProviderNames.XttsContainer, StringComparison.Ordinal);
        var isQwen = string.Equals(CurrentSettings.TtsProvider, ProviderNames.Qwen, StringComparison.Ordinal);
        if (!isXtts && !isQwen)
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

        var ffmpegPath = DependencyLocator.FindFfmpeg();
        if (ffmpegPath is null)
        {
            _log.Warning("ffmpeg not found — multi-speaker auto-reference extraction skipped.");
            return;
        }

        var sampleRate = isQwen ? "16000" : "24000";
        var providerTag = isQwen ? "qwen" : "xtts";
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

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(segStart.ToString("F3", CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(mediaPath);
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(extractDuration.ToString("F3", CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add(sampleRate);
            psi.ArgumentList.Add("-sample_fmt");
            psi.ArgumentList.Add("s16");
            psi.ArgumentList.Add(outputPath);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start ffmpeg for speaker '{speakerId}' reference extraction.");
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;

            if (proc.ExitCode != 0 || !File.Exists(outputPath))
            {
                _log.Warning($"Auto-reference extraction failed for speaker '{speakerId}': {stderr}");
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
