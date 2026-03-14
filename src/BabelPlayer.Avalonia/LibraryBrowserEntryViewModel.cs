using Avalonia;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public sealed record LibraryBrowserEntryViewModel
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsFolder { get; init; }
    public required bool IsExpanded { get; init; }
    public required bool CanExpand { get; init; }
    public required Thickness RowMargin { get; init; }

    public string ExpansionGlyph => IsExpanded ? "v" : ">";

    public string KindLabel => IsFolder ? "DIR" : "VID";

    public static LibraryBrowserEntryViewModel FromSnapshot(LibraryEntrySnapshot snapshot, int depth)
    {
        return new LibraryBrowserEntryViewModel
        {
            Name = snapshot.Name,
            Path = snapshot.Path,
            IsFolder = snapshot.IsFolder,
            IsExpanded = snapshot.IsExpanded,
            CanExpand = snapshot.IsFolder && (snapshot.HasUnrealizedChildren || snapshot.Children.Count > 0),
            RowMargin = new Thickness(depth * 16, 0, 0, 6)
        };
    }
}
