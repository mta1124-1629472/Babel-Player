using System.Collections.ObjectModel;

namespace BabelPlayer.WinUI;

public sealed class PlaylistViewModel : ObservableObject
{
    private int _currentIndex = -1;
    private bool _isVisible = true;
    private PlaylistItem? _selectedItem;

    public ObservableCollection<PlaylistItem> Items { get; } = [];

    public int CurrentIndex
    {
        get => _currentIndex;
        set => SetProperty(ref _currentIndex, value);
    }

    public PlaylistItem? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}
