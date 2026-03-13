namespace BabelPlayer.App;

public interface IShellLibraryService
{
    event Action<ShellLibrarySnapshot>? SnapshotChanged;

    ShellLibrarySnapshot Current { get; }

    bool IsSupportedMediaPath(string path);

    ShellLibraryMutationResult PinRoot(string path);

    ShellLibraryMutationResult UnpinRoot(string path);

    ShellLibraryMutationResult SetExpanded(string path, bool isExpanded);

    ShellLibraryMutationResult RefreshRoot(string path);
}

public sealed record LibraryEntrySnapshot
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsFolder { get; init; }
    public bool IsExpanded { get; init; }
    public bool HasUnrealizedChildren { get; init; }
    public IReadOnlyList<LibraryEntrySnapshot> Children { get; init; } = [];
}

public sealed record ShellLibrarySnapshot
{
    public IReadOnlyList<LibraryEntrySnapshot> Roots { get; init; } = [];
}

public sealed record ShellLibraryMutationResult(
    ShellLibrarySnapshot Snapshot,
    string? StatusMessage = null,
    bool IsError = false);

public sealed class ShellLibraryService : IShellLibraryService
{
    private readonly LibraryBrowserService _libraryBrowserService;
    private readonly IShellPreferencesService _shellPreferencesService;
    private readonly HashSet<string> _expandedPaths = new(StringComparer.OrdinalIgnoreCase);

    public ShellLibraryService(
        LibraryBrowserService libraryBrowserService,
        IShellPreferencesService shellPreferencesService)
    {
        _libraryBrowserService = libraryBrowserService;
        _shellPreferencesService = shellPreferencesService;
        Current = BuildSnapshot(_shellPreferencesService.Current.PinnedRoots);
    }

    public event Action<ShellLibrarySnapshot>? SnapshotChanged;

    public ShellLibrarySnapshot Current { get; private set; }

    public bool IsSupportedMediaPath(string path) => LibraryBrowserService.IsSupportedMediaFile(path);

    public ShellLibraryMutationResult PinRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return new ShellLibraryMutationResult(Current, $"Pinned root not found: {path}", true);
        }

        if (_shellPreferencesService.Current.PinnedRoots.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
        {
            return new ShellLibraryMutationResult(Current);
        }

        var updatedRoots = _shellPreferencesService.Current.PinnedRoots
            .Append(path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _shellPreferencesService.ApplyPinnedRootsChange(new ShellPinnedRootsChange(updatedRoots));
        return Publish(BuildSnapshot(updatedRoots), $"Pinned root added: {path}");
    }

    public ShellLibraryMutationResult UnpinRoot(string path)
    {
        var updatedRoots = _shellPreferencesService.Current.PinnedRoots
            .Where(existing => !string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (updatedRoots.Length == _shellPreferencesService.Current.PinnedRoots.Count)
        {
            return new ShellLibraryMutationResult(Current);
        }

        _expandedPaths.RemoveWhere(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)
            || existing.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        _shellPreferencesService.ApplyPinnedRootsChange(new ShellPinnedRootsChange(updatedRoots));
        return Publish(BuildSnapshot(updatedRoots), $"Pinned root removed: {path}");
    }

    public ShellLibraryMutationResult SetExpanded(string path, bool isExpanded)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ShellLibraryMutationResult(Current);
        }

        if (isExpanded)
        {
            _expandedPaths.Add(path);
        }
        else
        {
            _expandedPaths.RemoveWhere(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)
                || existing.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        return Publish(BuildSnapshot(_shellPreferencesService.Current.PinnedRoots));
    }

    public ShellLibraryMutationResult RefreshRoot(string path)
    {
        return Publish(BuildSnapshot(_shellPreferencesService.Current.PinnedRoots));
    }

    private ShellLibraryMutationResult Publish(ShellLibrarySnapshot snapshot, string? statusMessage = null, bool isError = false)
    {
        Current = snapshot;
        SnapshotChanged?.Invoke(Current);
        return new ShellLibraryMutationResult(Current, statusMessage, isError);
    }

    private ShellLibrarySnapshot BuildSnapshot(IReadOnlyList<string> pinnedRoots)
    {
        return new ShellLibrarySnapshot
        {
            Roots = pinnedRoots
                .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(BuildRootEntry)
                .ToArray()
        };
    }

    private LibraryEntrySnapshot BuildRootEntry(string rootPath)
    {
        var root = _libraryBrowserService.BuildRootNode(rootPath);
        return BuildEntry(root, rootPath);
    }

    private LibraryEntrySnapshot BuildEntry(LibraryNode node, string rootPath)
    {
        var isExpanded = _expandedPaths.Contains(node.Path);
        var children = Array.Empty<LibraryEntrySnapshot>();

        if (isExpanded && node.IsFolder)
        {
            children = _libraryBrowserService.BuildRootNode(node.Path).Children
                .Select(child => BuildEntry(child, rootPath))
                .ToArray();
        }
        else if (node.Children.Count > 0)
        {
            children = node.Children
                .Select(child => BuildEntry(child, rootPath))
                .ToArray();
        }

        return new LibraryEntrySnapshot
        {
            Name = node.Name,
            Path = node.Path,
            IsFolder = node.IsFolder,
            IsExpanded = isExpanded,
            HasUnrealizedChildren = node.IsFolder && !isExpanded,
            Children = children
        };
    }
}
