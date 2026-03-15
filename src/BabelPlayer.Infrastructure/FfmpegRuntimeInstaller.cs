using BabelPlayer.Core;
using System.Runtime.InteropServices;

namespace BabelPlayer.Infrastructure;

public static class FfmpegRuntimeInstaller
{
    public const string RuntimeVersion = "essentials-2026";
    public const string RuntimeSourceX64 = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    public const string ReleasePageUrl = "https://www.gyan.dev/ffmpeg/builds/";

    private static readonly HttpClient HttpClient = new(new HttpClientHandler { AllowAutoRedirect = true });

    /// <summary>Returns the ffmpeg binary name for the current OS ("ffmpeg" on Linux/macOS, "ffmpeg.exe" on Windows).</summary>
    private static string FfmpegBinaryName =>
        OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    private static string FfprobeBinaryName =>
        OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    public static string GetInstallDirectory(ISettingsStore settingsStore, Architecture architecture)
        => Path.Combine(settingsStore.GetAppDataDirectory(), "tools", "ffmpeg", RuntimeVersion, RuntimeArchitectureHelper.ToFolderName(architecture));

    public static string GetInstalledFfmpegPath(ISettingsStore settingsStore, Architecture architecture)
        => Path.Combine(GetInstallDirectory(settingsStore, architecture), FfmpegBinaryName);

    public static string GetInstalledFfmpegPath(ISettingsStore settingsStore)
        => GetInstalledFfmpegPath(settingsStore, RuntimeArchitectureHelper.GetCurrentArchitecture());

    public static string GetInstalledFfprobePath(ISettingsStore settingsStore, Architecture architecture)
        => Path.Combine(GetInstallDirectory(settingsStore, architecture), FfprobeBinaryName);

    public static string GetInstalledFfprobePath(ISettingsStore settingsStore)
        => GetInstalledFfprobePath(settingsStore, RuntimeArchitectureHelper.GetCurrentArchitecture());

    public static bool IsInstalled(ISettingsStore settingsStore, Architecture architecture)
        => File.Exists(GetInstalledFfmpegPath(settingsStore, architecture))
        && File.Exists(GetInstalledFfprobePath(settingsStore, architecture));

    public static bool IsInstalled(ISettingsStore settingsStore)
        => IsInstalled(settingsStore, RuntimeArchitectureHelper.GetCurrentArchitecture());

    public static async Task<string> InstallAsync(
        ISettingsStore settingsStore,
        Architecture architecture,
        Action<RuntimeInstallProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var finalFfmpeg = GetInstalledFfmpegPath(settingsStore, architecture);
        if (IsInstalled(settingsStore, architecture))
        {
            onProgress?.Invoke(new RuntimeInstallProgress { Stage = "ready" });
            return finalFfmpeg;
        }

        var finalDirectory = GetInstallDirectory(settingsStore, architecture);
        var tempRoot = Path.Combine(settingsStore.GetAppDataDirectory(), "temp", "ffmpeg", $"{RuntimeVersion}-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(tempRoot, "ffmpeg-runtime.zip");
        var extractDirectory = Path.Combine(tempRoot, "extract");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(extractDirectory);

        try
        {
            await DownloadArchiveAsync(GetRuntimeSource(architecture), zipPath, onProgress, cancellationToken);
            await ExtractRuntimeArchiveAsync(zipPath, extractDirectory, onProgress, cancellationToken);

            var extractedFfmpeg = Directory
                .GetFiles(extractDirectory, FfmpegBinaryName, SearchOption.AllDirectories)
                .FirstOrDefault();
            var extractedFfprobe = Directory
                .GetFiles(extractDirectory, FfprobeBinaryName, SearchOption.AllDirectories)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(extractedFfmpeg) || string.IsNullOrWhiteSpace(extractedFfprobe))
                throw new InvalidOperationException(
                    $"The downloaded ffmpeg package did not contain {FfmpegBinaryName} and {FfprobeBinaryName}.");

            var binRoot = Path.GetDirectoryName(extractedFfmpeg)
                ?? throw new InvalidOperationException("Unable to locate extracted ffmpeg directory.");

            if (Directory.Exists(finalDirectory))
                Directory.Delete(finalDirectory, recursive: true);

            CopyDirectory(binRoot, finalDirectory);

            // On Linux/macOS ensure the binaries are executable
            if (!OperatingSystem.IsWindows())
            {
                SetExecutable(Path.Combine(finalDirectory, FfmpegBinaryName));
                SetExecutable(Path.Combine(finalDirectory, FfprobeBinaryName));
            }

            onProgress?.Invoke(new RuntimeInstallProgress { Stage = "ready" });
            return finalFfmpeg;
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
            Architecture.Arm64 => throw new NotSupportedException(
                "ffmpeg ARM64 bootstrap source is not configured yet."),
            _ => throw new NotSupportedException(
                $"ffmpeg runtime bootstrap does not support architecture '{architecture}'.")
        };
    }

    private static void SetExecutable(string path)
    {
        try
        {
            // chmod +x equivalent via mono/dotnet on Unix
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(3000);
        }
        catch { /* non-fatal */ }
    }

    private static async Task DownloadArchiveAsync(
        string sourceUrl, string destinationPath,
        Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            sourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

    private static async Task ExtractRuntimeArchiveAsync(
        string archivePath, string extractDirectory,
        Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
        var fileEntries = archive.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToList();
        var total = fileEntries.Count;
        var completed = 0;

        foreach (var entry in fileEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dest = Path.Combine(extractDirectory, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await using var src = entry.Open();
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst, cancellationToken);
            completed++;
            onProgress?.Invoke(new RuntimeInstallProgress
            {
                Stage = "extracting",
                ItemsCompleted = completed,
                TotalItems = total
            });
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var dest = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
