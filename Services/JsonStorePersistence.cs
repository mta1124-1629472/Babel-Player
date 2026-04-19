using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

internal static class JsonStorePersistence
{
    public static void AtomicWriteText(string finalPath, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        AtomicWriteBytes(finalPath, bytes);
    }

    public static async Task AtomicWriteTextAsync(
        string finalPath,
        string content,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await AtomicWriteBytesAsync(finalPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    public static void AtomicWriteBytes(string finalPath, byte[] content)
    {
        var dir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var tempPath = BuildTempPath(finalPath);
        try
        {
            File.WriteAllBytes(tempPath, content);
            ArtifactPersistence.AtomicReplace(tempPath, finalPath);
        }
        finally
        {
            ArtifactPersistence.TryDelete(tempPath);
        }
    }

    public static async Task AtomicWriteBytesAsync(
        string finalPath,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var tempPath = BuildTempPath(finalPath);
        try
        {
            await File.WriteAllBytesAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            ArtifactPersistence.AtomicReplace(tempPath, finalPath);
        }
        finally
        {
            ArtifactPersistence.TryDelete(tempPath);
        }
    }

    public static string MoveUnreadableFileToBackup(string path)
    {
        var backupPath = $"{path}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        File.Move(path, backupPath, overwrite: true);
        return backupPath;
    }

    private static string BuildTempPath(string finalPath) =>
        $"{finalPath}.{Guid.NewGuid():N}.tmp";
}
