using BabelPlayer.Core;
using System.Diagnostics;

namespace BabelPlayer.App;

/// <summary>
/// Cross-platform audio extractor that shells out to the ffmpeg CLI.
/// Used on Linux/macOS, and as a fallback on Windows if Media Foundation is unavailable.
/// </summary>
public sealed class FfmpegAudioExtractor : IAudioExtractor
{
    private readonly string _ffmpegPath;

    /// <param name="ffmpegPath">
    /// Full path to the ffmpeg executable, or just "ffmpeg" if it is on PATH.
    /// </param>
    public FfmpegAudioExtractor(string ffmpegPath = "ffmpeg")
    {
        _ffmpegPath = ffmpegPath;
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                proc?.WaitForExit(2000);
                return proc?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public string Extract(string mediaPath)
    {
        MediaFoundationAudioExtractor.ValidateInput(mediaPath);

        var outputPath = MediaFoundationAudioExtractor.MakeTempWavPath(mediaPath);

        // -y        overwrite output
        // -i        input file
        // -vn       drop video
        // -ar 16000 resample to 16 kHz
        // -ac 1     mono
        // -sample_fmt s16  16-bit PCM
        var args = $"-y -i \"{mediaPath}\" -vn -ar 16000 -ac 1 -sample_fmt s16 \"{outputPath}\"";

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = args,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start ffmpeg process.");

        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            TryDelete(outputPath);
            throw new InvalidOperationException(
                $"ffmpeg exited with code {proc.ExitCode}.\n{stderr}");
        }

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"ffmpeg succeeded but output WAV was not created: {outputPath}");
        }

        return outputPath;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
