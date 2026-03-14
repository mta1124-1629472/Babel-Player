using BabelPlayer.App;
using System.Runtime.InteropServices;

namespace BabelPlayer.Infrastructure;

public static class LlamaCppRuntimeInstaller
{
    public const string RuntimeVersion = "b8234";
    public const string RuntimeSource = "auto";
    public const string DownloadUrlX64 = "https://github.com/ggml-org/llama.cpp/releases/download/b8234/llama-b8234-bin-win-cpu-x64.zip";
    public const string DownloadUrlArm64 = "https://github.com/ggml-org/llama.cpp/releases/download/b8234/llama-b8234-bin-win-arm64.zip";
    public const string ReleasePageUrl = "https://github.com/ggml-org/llama.cpp/releases/tag/b8234";
    public const string HyMt18BPageUrl = "https://huggingface.co/tencent/HY-MT1.5-1.8B-GGUF";
    public const string HyMt7BPageUrl = "https://huggingface.co/tencent/HY-MT1.5-7B-GGUF";

    private static readonly HttpClient HttpClient = new();

    public static string GetInstallDirectory(Architecture architecture)
    {
        return Path.Combine(SecureSettingsStore.GetAppDataDirectory(), "tools", "llama.cpp", RuntimeVersion, RuntimeArchitectureHelper.ToFolderName(architecture));
    }

    public static string GetInstalledServerPath(Architecture architecture)
    {
        return Path.Combine(GetInstallDirectory(architecture), "llama-server.exe");
    }

    public static string GetInstalledServerPath()
        => GetInstalledServerPath(RuntimeArchitectureHelper.GetCurrentArchitecture());

    public static bool IsInstalled(Architecture architecture)
    {
        return File.Exists(GetInstalledServerPath(architecture));
    }

    public static bool IsInstalled()
    {
        return IsInstalled(RuntimeArchitectureHelper.GetCurrentArchitecture());
    }

    public static async Task<string> InstallAsync(Architecture architecture, Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
    {
        var finalDirectory = GetInstallDirectory(architecture);
        var finalServerPath = GetInstalledServerPath(architecture);
        if (File.Exists(finalServerPath))
        {
            onProgress?.Invoke(new RuntimeInstallProgress { Stage = "ready" });
            return finalServerPath;
        }

        var tempRoot = Path.Combine(SecureSettingsStore.GetAppDataDirectory(), "temp", "llama.cpp", $"{RuntimeVersion}-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(tempRoot, "llama-runtime.zip");
        var extractDirectory = Path.Combine(tempRoot, "extract");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(extractDirectory);

        try
        {
            await DownloadRuntimeArchiveAsync(GetDownloadUrl(architecture), zipPath, onProgress, cancellationToken);
            await ExtractRuntimeArchiveAsync(zipPath, extractDirectory, onProgress, cancellationToken);

            var extractedServerPath = Directory
                .GetFiles(extractDirectory, "llama-server.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(extractedServerPath))
            {
                throw new InvalidOperationException("The downloaded llama.cpp package did not contain llama-server.exe.");
            }

            var extractedRoot = Path.GetDirectoryName(extractedServerPath)
                ?? throw new InvalidOperationException("Unable to locate the extracted llama.cpp runtime directory.");

            if (Directory.Exists(finalDirectory))
            {
                Directory.Delete(finalDirectory, recursive: true);
            }

            CopyDirectory(extractedRoot, finalDirectory);
            onProgress?.Invoke(new RuntimeInstallProgress { Stage = "ready" });
            return finalServerPath;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static string GetDownloadUrl(Architecture architecture)
    {
        return architecture switch
        {
            Architecture.X64 => DownloadUrlX64,
            Architecture.Arm64 => DownloadUrlArm64,
            _ => throw new NotSupportedException($"llama.cpp runtime bootstrap does not support architecture '{architecture}'.")
        };
    }

    private static async Task DownloadRuntimeArchiveAsync(string sourceUrl, string destinationPath, Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
        using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
        var fileEntries = archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)).ToList();
        var totalItems = fileEntries.Count;
        var completed = 0;

        foreach (var entry in fileEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destinationPath = Path.Combine(extractDirectory, entry.FullName);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

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

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            var destinationParent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }

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
