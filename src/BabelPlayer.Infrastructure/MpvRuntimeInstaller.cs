using BabelPlayer.App;
using SharpCompress.Archives;
using SharpCompress.Common;
using System.Runtime.InteropServices;

namespace BabelPlayer.Infrastructure;

public static class MpvRuntimeInstaller
{
    public const string RuntimeVersion = "0.41.0";
    public const string RuntimeSourceX64 = "https://sourceforge.net/projects/mpv-player-windows/files/release/mpv-0.41.0-x86_64-v3.7z/download";
    public const string RuntimeSourceArm64 = "https://sourceforge.net/projects/mpv-player-windows/files/release/mpv-0.41.0-aarch64-v3.7z/download";
    public const string ReleasePageUrl = "https://sourceforge.net/projects/mpv-player-windows/files/release/";

    private static readonly HttpClient HttpClient = new(new HttpClientHandler { AllowAutoRedirect = true });

    public static string GetInstallDirectory(Architecture architecture)
        => Path.Combine(SecureSettingsStore.GetAppDataDirectory(), "tools", "mpv", RuntimeVersion, RuntimeArchitectureHelper.ToFolderName(architecture));

    public static string GetInstalledExePath(Architecture architecture) => Path.Combine(GetInstallDirectory(architecture), "mpv.exe");

    public static string GetInstalledExePath() => GetInstalledExePath(RuntimeArchitectureHelper.GetCurrentArchitecture());

    public static bool IsInstalled(Architecture architecture) => File.Exists(GetInstalledExePath(architecture));

    public static bool IsInstalled() => IsInstalled(RuntimeArchitectureHelper.GetCurrentArchitecture());

    public static async Task<string> InstallAsync(Architecture architecture, Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
    {
        var finalDirectory = GetInstallDirectory(architecture);
        var finalExePath = GetInstalledExePath(architecture);
        if (File.Exists(finalExePath))
        {
            onProgress?.Invoke(new RuntimeInstallProgress { Stage = "ready" });
            return finalExePath;
        }

        var tempRoot = Path.Combine(SecureSettingsStore.GetAppDataDirectory(), "temp", "mpv", $"{RuntimeVersion}-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(tempRoot, "mpv-runtime.7z");
        var extractDirectory = Path.Combine(tempRoot, "extract");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(extractDirectory);

        try
        {
            await DownloadRuntimeArchiveAsync(GetRuntimeSource(architecture), archivePath, onProgress, cancellationToken);
            await ExtractRuntimeArchiveAsync(archivePath, extractDirectory, onProgress, cancellationToken);

            var extractedExePath = Directory.GetFiles(extractDirectory, "mpv.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(extractedExePath))
            {
                throw new InvalidOperationException("The downloaded mpv package did not contain mpv.exe.");
            }

            var extractedRoot = Path.GetDirectoryName(extractedExePath)
                ?? throw new InvalidOperationException("Unable to locate the extracted mpv runtime directory.");

            if (Directory.Exists(finalDirectory))
            {
                Directory.Delete(finalDirectory, recursive: true);
            }

            CopyDirectory(extractedRoot, finalDirectory);
            onProgress?.Invoke(new RuntimeInstallProgress { Stage = "ready" });
            return finalExePath;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static string GetRuntimeSource(Architecture architecture)
    {
        return architecture switch
        {
            Architecture.X64 => RuntimeSourceX64,
            Architecture.Arm64 => RuntimeSourceArm64,
            _ => throw new NotSupportedException($"mpv runtime bootstrap does not support architecture '{architecture}'.")
        };
    }

    private static async Task DownloadRuntimeArchiveAsync(string url, string destinationPath, Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
        using var archive = ArchiveFactory.Open(archivePath);
        var entries = archive.Entries.Where(entry => !entry.IsDirectory).ToList();
        var totalItems = entries.Count;
        var completed = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.WriteToDirectory(extractDirectory, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });
            completed++;
            onProgress?.Invoke(new RuntimeInstallProgress
            {
                Stage = "extracting",
                ItemsCompleted = completed,
                TotalItems = totalItems
            });
            await Task.Yield();
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
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
