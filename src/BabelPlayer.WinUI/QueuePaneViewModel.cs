using System.Collections.ObjectModel;

namespace BabelPlayer.WinUI;

public sealed class QueuePaneViewModel : ObservableObject
{
    private bool _isVisible = true;
    private PlaylistItem? _nowPlayingItem;
    private PlaylistItem? _selectedQueueItem;
    private PlaylistItem? _selectedHistoryItem;

    public ObservableCollection<PlaylistItem> QueueItems { get; } = [];

    public ObservableCollection<PlaylistItem> HistoryItems { get; } = [];

    public PlaylistItem? NowPlayingItem
    {
        get => _nowPlayingItem;
        set => SetProperty(ref _nowPlayingItem, value);
    }

    public PlaylistItem? SelectedQueueItem
    {
        get => _selectedQueueItem;
        set => SetProperty(ref _selectedQueueItem, value);
    }

    public PlaylistItem? SelectedHistoryItem
    {
        get => _selectedHistoryItem;
        set => SetProperty(ref _selectedHistoryItem, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}
