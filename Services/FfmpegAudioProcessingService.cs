using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// A real implementation of IAudioProcessingService that uses ffmpeg.
/// </summary>
public sealed class FfmpegAudioProcessingService : IAudioProcessingService
{
    private readonly AppLog _log;

    public FfmpegAudioProcessingService(AppLog log)
    {
        _log = log;
    }

    public async Task CombineAudioSegmentsAsync(
        IReadOnlyList<string> segmentAudioPaths,
        string outputAudioPath,
        CancellationToken cancellationToken)
    {
        if (segmentAudioPaths.Count == 0)
            throw new InvalidOperationException("Cannot combine zero segment audio files.");

        var outputDir = Path.GetDirectoryName(outputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        if (segmentAudioPaths.Count == 1)
        {
            File.Copy(segmentAudioPaths[0], outputAudioPath, overwrite: true);
            return;
        }

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg not found. Combined output requires ffmpeg.");

        var concatListDir = Path.Combine(Path.GetTempPath(), $"babel-concat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(concatListDir);
        var concatListPath = Path.Combine(concatListDir, "inputs.txt");
        var concatFile = string.Join(
            Environment.NewLine,
            segmentAudioPaths.Select(path => $"file '{EscapeConcatListPath(path)}'"));

        await File.WriteAllTextAsync(concatListPath, concatFile, cancellationToken).ConfigureAwait(false);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("concat");
            psi.ArgumentList.Add("-safe");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(concatListPath);
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("libmp3lame");
            psi.ArgumentList.Add("-q:a");
            psi.ArgumentList.Add("3");
            psi.ArgumentList.Add(outputAudioPath);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffmpeg for segment concatenation.");

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort termination
                }
            });

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(outputAudioPath))
            {
                throw new InvalidOperationException(
                    $"ffmpeg concatenation failed or produced no output file (exit code {process.ExitCode}): {stderr} {stdout}".Trim());
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(concatListDir))
                    Directory.Delete(concatListDir, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }

    public async Task ExtractAudioClipAsync(
        string inputPath,
        string outputPath,
        double startTimeSeconds,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg not found. Audio extraction requires ffmpeg.");

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-y");
        
        // Use -ss before -i for faster seeking
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(startTimeSeconds.ToString("F3", CultureInfo.InvariantCulture));
        
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(durationSeconds.ToString("F3", CultureInfo.InvariantCulture));
        
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000"); // Standard for speech inference
        psi.ArgumentList.Add("-sample_fmt");
        psi.ArgumentList.Add("s16");
        psi.ArgumentList.Add(outputPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for audio extraction.");

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort termination
            }
        });

        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (proc.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException($"ffmpeg audio extraction failed with exit code {proc.ExitCode}: {stderr}");
        }
    }

    private static string EscapeConcatListPath(string path) =>
        path.Replace("\\", "/", StringComparison.Ordinal)
            .Replace("'", "'\\''", StringComparison.Ordinal);
}