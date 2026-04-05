using System;
using System.Collections.Generic;
using System.Diagnostics;
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
}
