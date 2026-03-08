using BabelPlayer.Core;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BabelPlayer.UI;

internal static class SubtitleImportService
{
    public static async Task<IReadOnlyList<SubtitleCue>> LoadExternalSubtitleCuesAsync(
        string path,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        if (string.Equals(Path.GetExtension(path), ".srt", StringComparison.OrdinalIgnoreCase))
        {
            return SubtitleFileService.ParseSrt(path);
        }

        var ffmpegPath = await EnsureFfmpegAsync(onRuntimeProgress, onStatus, cancellationToken);
        var tempPath = Path.Combine(GetCacheDirectory(), $"{Guid.NewGuid():N}.srt");
        onStatus?.Invoke($"Converting {Path.GetFileName(path)} to SRT...");
        await RunFfmpegAsync(ffmpegPath, $"-y -i \"{path}\" -c:s srt \"{tempPath}\"", cancellationToken);
        return SubtitleFileService.ParseSrt(tempPath);
    }

    public static async Task<IReadOnlyList<SubtitleCue>> ExtractEmbeddedSubtitleCuesAsync(
        string videoPath,
        MediaTrackInfo track,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        if (!track.IsTextBased)
        {
            throw new InvalidOperationException("Only text-based subtitle tracks can be imported into the Babel Player overlay.");
        }

        if (track.FfIndex is null)
        {
            throw new InvalidOperationException("The selected subtitle track cannot be mapped through ffmpeg.");
        }

        var cachePath = GetEmbeddedTrackCachePath(videoPath, track);
        if (!File.Exists(cachePath))
        {
            var ffmpegPath = await EnsureFfmpegAsync(onRuntimeProgress, onStatus, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            onStatus?.Invoke($"Extracting embedded subtitle track {track.Id}...");
            await RunFfmpegAsync(ffmpegPath, $"-y -i \"{videoPath}\" -map 0:{track.FfIndex.Value} -c:s srt \"{cachePath}\"", cancellationToken);
        }

        return SubtitleFileService.ParseSrt(cachePath);
    }

    private static async Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onRuntimeProgress, Action<string>? onStatus, CancellationToken cancellationToken)
    {
        if (!FfmpegRuntimeInstaller.IsInstalled())
        {
            onStatus?.Invoke("Downloading ffmpeg runtime...");
        }

        return await FfmpegRuntimeInstaller.InstallAsync(onRuntimeProgress, cancellationToken);
    }

    private static async Task RunFfmpegAsync(string ffmpegPath, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };

        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdErr = await stdErrTask;
        _ = await stdOutTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stdErr)
                ? "ffmpeg subtitle import failed."
                : $"ffmpeg subtitle import failed: {stdErr.Trim()}");
        }
    }

    private static string GetEmbeddedTrackCachePath(string videoPath, MediaTrackInfo track)
    {
        using var sha = SHA256.Create();
        var identity = $"{videoPath}|{File.GetLastWriteTimeUtc(videoPath).Ticks}|{new FileInfo(videoPath).Length}|{track.FfIndex}|{track.Codec}";
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(GetCacheDirectory(), "embedded", $"{hash}.srt");
    }

    private static string GetCacheDirectory() => Path.Combine(SecureSettingsStore.GetAppDataDirectory(), "cache", "subtitles");
}
