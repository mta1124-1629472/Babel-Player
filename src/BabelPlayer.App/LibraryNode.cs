using System.Collections.ObjectModel;

namespace BabelPlayer.App;

public sealed class LibraryNode : ObservableObject
{
    private bool _isExpanded;

    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsFolder { get; init; }

    public ObservableCollection<LibraryNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public override string ToString() => Name;
}
