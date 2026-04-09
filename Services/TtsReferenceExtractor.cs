using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Extracts a clean speech sample from a source video file for use as TTS reference audio.
/// Outputs 16kHz mono WAV format as required by TTS models.
/// </summary>
public sealed class TtsReferenceExtractor : IAsyncDisposable
{
    private readonly AppLog _log;
    private string? _tempWavPath;
    private bool _disposed;

    public TtsReferenceExtractor(AppLog log)
    {
        _log = log;
    }

    /// <summary>
    /// Extracts the first 30 seconds (or less if file is shorter) of audio from a video file.
    /// Returns the path to the extracted 16kHz mono WAV file.
    /// The caller is responsible for cleaning up the returned file, or calling DisposeAsync on this instance.
    /// </summary>
    public async Task<string> ExtractReferenceAsync(string videoPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            throw new ArgumentException("Video path cannot be empty", nameof(videoPath));

        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Source video file not found", videoPath);

        // Clean up any previously extracted reference
        await CleanupAsync();

        var ffmpegPath = ResolveFfmpegPath()
            ?? throw new InvalidOperationException("ffmpeg not found. TTS reference extraction requires ffmpeg.");

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"babel_tts_ref_{Guid.NewGuid():N}.wav");

        _log.Info($"[TtsReferenceExtractor] Extracting reference audio from: {videoPath}");
        _log.Info($"[TtsReferenceExtractor] Output path: {tempPath}");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Extract first 30 seconds, 16kHz mono WAV (TTS requirement)
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("wav");
        psi.ArgumentList.Add(tempPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for TTS reference extraction.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg reference extraction failed with exit code {process.ExitCode}: {stderr} {stdout}".Trim());
        }

        if (!File.Exists(tempPath))
        {
            throw new InvalidOperationException(
                $"ffmpeg completed but output file was not created: {tempPath}");
        }

        _tempWavPath = tempPath;
        _log.Info($"[TtsReferenceExtractor] Reference extraction complete: {tempPath}");

        return tempPath;
    }

    /// <summary>
    /// Deletes the extracted reference WAV file if one exists.
    /// </summary>
    public Task DeleteAsync()
    {
        return CleanupAsync();
    }

    /// <summary>
    /// Resets the extractor state and deletes any temp file.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await CleanupAsync();
        _disposed = true;
    }

    private Task CleanupAsync()
    {
        if (string.IsNullOrWhiteSpace(_tempWavPath))
            return Task.CompletedTask;

        try
        {
            if (File.Exists(_tempWavPath))
            {
                File.Delete(_tempWavPath);
                _log.Info($"[TtsReferenceExtractor] Cleaned up temp file: {_tempWavPath}");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[TtsReferenceExtractor] Failed to clean up temp file: {ex.Message}", ex);
        }
        finally
        {
            _tempWavPath = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves ffmpeg path using the same precedence as the Python server:
    /// 1. Next to the executable
    /// 2. tools/win-x64/ffmpeg.exe
    /// 3. PATH
    /// </summary>
    private static string? ResolveFfmpegPath()
    {
        var appDir = AppContext.BaseDirectory;

        // Check next to executable first
        var localFfmpeg = Path.Combine(appDir, "ffmpeg.exe");
        if (File.Exists(localFfmpeg))
            return localFfmpeg;

        // Check tools/win-x64
        var toolsFfmpeg = Path.Combine(appDir, "tools", "win-x64", "ffmpeg.exe");
        if (File.Exists(toolsFfmpeg))
            return toolsFfmpeg;

        // Fallback to PATH
        return ResolveFromPath("ffmpeg");
    }

    private static string? ResolveFromPath(string command)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", string.Empty }
            : new[] { string.Empty };

        var dirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in dirs)
        {
            var trimmedDir = dir.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(trimmedDir))
                continue;

            foreach (var ext in extensions)
            {
                var fullPath = Path.Combine(trimmedDir, command + ext);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        return null;
    }
}
