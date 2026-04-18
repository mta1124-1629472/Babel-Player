using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

internal static class ArtifactPersistence
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string GetManifestPath(string artifactPath) => $"{artifactPath}.manifest.json";

    public static async Task AtomicWriteTextAsync(
        string finalPath,
        string content,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await AtomicWriteBytesAsync(finalPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    public static async Task AtomicWriteBytesAsync(
        string finalPath,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var tempPath = $"{finalPath}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            AtomicReplace(tempPath, finalPath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public static async Task WriteManifestAsync<TManifest>(
        string artifactPath,
        TManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = GetManifestPath(artifactPath);
        var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        await AtomicWriteTextAsync(manifestPath, json, cancellationToken).ConfigureAwait(false);
    }

    public static void AtomicReplace(string sourcePath, string destinationPath)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(destinationPath))
        {
            var backupPath = $"{destinationPath}.bak";
            TryDelete(backupPath);
            File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true);
            TryDelete(backupPath);
            return;
        }

        File.Move(sourcePath, destinationPath);
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
