using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public static class AudioConcatUtility
{
    public static async Task CombineAudioSegmentsAsync(
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

        await File.WriteAllTextAsync(concatListPath, concatFile, cancellationToken);

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

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg concatenation failed with exit code {process.ExitCode}: {stderr} {stdout}".Trim());
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
            }
        }
    }

    private static string EscapeConcatListPath(string path) =>
        path.Replace("\\", "/", StringComparison.Ordinal)
            .Replace("'", "'\\''", StringComparison.Ordinal);
}