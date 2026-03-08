using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace BabelPlayer.UI;

internal static class FfmpegRuntimeInstaller
{
    public const string RuntimeVersion = "essentials-2026";
    public const string RuntimeSource = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    public const string ReleasePageUrl = "https://www.gyan.dev/ffmpeg/builds/";

    private static readonly HttpClient HttpClient = new(new HttpClientHandler { AllowAutoRedirect = true });

    public static string GetInstallDirectory() => Path.Combine(SecureSettingsStore.GetAppDataDirectory(), "tools", "ffmpeg", RuntimeVersion);
    public static string GetInstalledFfmpegPath() => Path.Combine(GetInstallDirectory(), "ffmpeg.exe");
    public static string GetInstalledFfprobePath() => Path.Combine(GetInstallDirectory(), "ffprobe.exe");
    public static bool IsInstalled() => File.Exists(GetInstalledFfmpegPath()) && File.Exists(GetInstalledFfprobePath());

    public static async Task<string> InstallAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
    {
        var finalDirectory = GetInstallDirectory();
        var finalFfmpeg = GetInstalledFfmpegPath();
        if (IsInstalled())
        {
            onProgress?.Invoke(new RuntimeInstallProgress { Stage = "ready" });
            return finalFfmpeg;
        }

        var tempRoot = Path.Combine(SecureSettingsStore.GetAppDataDirectory(), "temp", "ffmpeg", $"{RuntimeVersion}-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(tempRoot, "ffmpeg-runtime.zip");
        var extractDirectory = Path.Combine(tempRoot, "extract");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(extractDirectory);

        try
        {
            await DownloadArchiveAsync(zipPath, onProgress, cancellationToken);
            await ExtractRuntimeArchiveAsync(zipPath, extractDirectory, onProgress, cancellationToken);

            var extractedFfmpeg = Directory.GetFiles(extractDirectory, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            var extractedFfprobe = Directory.GetFiles(extractDirectory, "ffprobe.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(extractedFfmpeg) || string.IsNullOrWhiteSpace(extractedFfprobe))
            {
                throw new InvalidOperationException("The downloaded ffmpeg package did not contain ffmpeg.exe and ffprobe.exe.");
            }

            var binRoot = Path.GetDirectoryName(extractedFfmpeg) ?? throw new InvalidOperationException("Unable to locate extracted ffmpeg directory.");
            if (Directory.Exists(finalDirectory))
            {
                Directory.Delete(finalDirectory, recursive: true);
            }

            CopyDirectory(binRoot, finalDirectory);
            onProgress?.Invoke(new RuntimeInstallProgress { Stage = "ready" });
            return finalFfmpeg;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task DownloadArchiveAsync(string destinationPath, Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(RuntimeSource, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(destinationPath);
        var buffer = new byte[1024 * 128];
        long transferred = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            transferred += bytesRead;
            onProgress?.Invoke(new RuntimeInstallProgress
            {
                Stage = "downloading",
                BytesTransferred = transferred,
                TotalBytes = totalBytes
            });
        }

        await destination.FlushAsync(cancellationToken);
    }

    private static async Task ExtractRuntimeArchiveAsync(string archivePath, string extractDirectory, Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var fileEntries = archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)).ToList();
        var totalItems = fileEntries.Count;
        var completed = 0;

        foreach (var entry in fileEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationPath = Path.Combine(extractDirectory, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var source = entry.Open();
            await using var destination = File.Create(destinationPath);
            await source.CopyToAsync(destination, cancellationToken);
            completed++;
            onProgress?.Invoke(new RuntimeInstallProgress
            {
                Stage = "extracting",
                ItemsCompleted = completed,
                TotalItems = totalItems
            });
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
