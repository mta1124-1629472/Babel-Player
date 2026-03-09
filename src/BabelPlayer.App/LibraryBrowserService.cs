using System.IO;

namespace BabelPlayer.App;

public sealed class LibraryBrowserService
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv",
        ".mp4",
        ".avi",
        ".mov",
        ".m4v",
        ".webm",
        ".mp3",
        ".wav",
        ".flac",
        ".ogg"
    };

    public IReadOnlyList<LibraryNode> BuildPinnedRoots(IEnumerable<string> pinnedRoots)
    {
        ArgumentNullException.ThrowIfNull(pinnedRoots);

        return pinnedRoots
            .Where(Directory.Exists)
            .Select(BuildRootNode)
            .ToList();
    }

    public LibraryNode BuildRootNode(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var directoryInfo = new DirectoryInfo(rootPath);
        var node = new LibraryNode
        {
            Name = directoryInfo.Name,
            Path = directoryInfo.FullName,
            IsFolder = true
        };

        foreach (var child in EnumerateChildren(directoryInfo.FullName))
        {
            node.Children.Add(child);
        }

        return node;
    }

    public IReadOnlyList<string> EnumerateMediaFiles(string folderPath, bool recursive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (!Directory.Exists(folderPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(folderPath, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(IsSupportedMediaFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsSupportedMediaFile(string path)
        => MediaExtensions.Contains(Path.GetExtension(path));

    private static IEnumerable<LibraryNode> EnumerateChildren(string path)
    {
        foreach (var directory in Directory.EnumerateDirectories(path).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var info = new DirectoryInfo(directory);
            yield return new LibraryNode
            {
                Name = info.Name,
                Path = info.FullName,
                IsFolder = true
            };
        }

        foreach (var file in Directory.EnumerateFiles(path).Where(IsSupportedMediaFile).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            yield return new LibraryNode
            {
                Name = Path.GetFileName(file),
                Path = file,
                IsFolder = false
            };
        }
    }
}
