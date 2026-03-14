using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public partial class MainWindow
{
    private const string PlaylistDragDataFormat = "com.babelplayer.avalonia.playlistpath";
    private static readonly DataFormat<string> PlaylistDragFormat = DataFormat.CreateStringApplicationFormat(PlaylistDragDataFormat);

    private readonly ObservableCollection<LibraryBrowserEntryViewModel> _libraryEntries = [];
    private readonly ObservableCollection<ShellPlaylistItem> _playlistQueueItems = [];

    private Border? _browserPanel;
    private Border? _playlistPanel;
    private Button? _browserPanelToggleButton;
    private Button? _playlistPanelToggleButton;
    private TextBlock? _playlistSummaryTextBlock;
    private TextBlock? _nowPlayingQueueTextBlock;
    private ItemsControl? _libraryEntriesItemsControl;
    private ListBox? _playlistQueueListBox;
    private ShellQueueSnapshot _currentQueueSnapshot = new();
    private ShellLibrarySnapshot _currentLibrarySnapshot = new();
    private string? _draggedQueuePath;
    private ShellPlaylistItem? _draggedQueueItem;

    private void InitializePanelControls()
    {
        _browserPanel ??= this.FindControl<Border>("BrowserPanel");
        _playlistPanel ??= this.FindControl<Border>("PlaylistPanel");
        _browserPanelToggleButton ??= this.FindControl<Button>("BrowserPanelToggleButton");
        _playlistPanelToggleButton ??= this.FindControl<Button>("PlaylistPanelToggleButton");
        _playlistSummaryTextBlock ??= this.FindControl<TextBlock>("PlaylistSummaryTextBlock");
        _nowPlayingQueueTextBlock ??= this.FindControl<TextBlock>("NowPlayingQueueTextBlock");
        _libraryEntriesItemsControl ??= this.FindControl<ItemsControl>("LibraryEntriesItemsControl");
        _playlistQueueListBox ??= this.FindControl<ListBox>("PlaylistQueueListBox");

        if (_libraryEntriesItemsControl is not null)
        {
            _libraryEntriesItemsControl.ItemsSource = _libraryEntries;
        }

        if (_playlistQueueListBox is not null)
        {
            _playlistQueueListBox.ItemsSource = _playlistQueueItems;
            _playlistQueueListBox.AddHandler(DragDrop.DragOverEvent, PlaylistQueueListBox_DragOver);
            _playlistQueueListBox.AddHandler(DragDrop.DropEvent, PlaylistQueueListBox_Drop);
        }

        _shell.ShellPreferencesService.SnapshotChanged -= HandlePreferencesSnapshotChanged;
        _shell.ShellPreferencesService.SnapshotChanged += HandlePreferencesSnapshotChanged;
        _shell.ShellLibraryService.SnapshotChanged -= HandleLibrarySnapshotChanged;
        _shell.ShellLibraryService.SnapshotChanged += HandleLibrarySnapshotChanged;
        _shell.QueueProjectionReader.QueueSnapshotChanged -= HandleQueueSnapshotChanged;
        _shell.QueueProjectionReader.QueueSnapshotChanged += HandleQueueSnapshotChanged;

        ApplyPreferencesSnapshot(_shell.ShellPreferencesService.Current);
        ApplyLibrarySnapshot(_shell.ShellLibraryService.Current);
        ApplyQueueSnapshot(_shell.QueueProjectionReader.QueueSnapshot);
    }

    private void DisposePanelControls()
    {
        _shell.ShellPreferencesService.SnapshotChanged -= HandlePreferencesSnapshotChanged;
        _shell.ShellLibraryService.SnapshotChanged -= HandleLibrarySnapshotChanged;
        _shell.QueueProjectionReader.QueueSnapshotChanged -= HandleQueueSnapshotChanged;
    }

    private void HandlePreferencesSnapshotChanged(ShellPreferencesSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => ApplyPreferencesSnapshot(snapshot));
    }

    private void HandleLibrarySnapshotChanged(ShellLibrarySnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => ApplyLibrarySnapshot(snapshot));
    }

    private void HandleQueueSnapshotChanged(ShellQueueSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => ApplyQueueSnapshot(snapshot));
    }

    private void ApplyPreferencesSnapshot(ShellPreferencesSnapshot snapshot)
    {
        if (_browserPanel is not null)
        {
            _browserPanel.IsVisible = snapshot.ShowBrowserPanel;
        }

        if (_playlistPanel is not null)
        {
            _playlistPanel.IsVisible = snapshot.ShowPlaylistPanel;
        }

        if (_browserPanelToggleButton is not null)
        {
            _browserPanelToggleButton.Content = snapshot.ShowBrowserPanel ? "Hide Browser" : "Show Browser";
        }

        if (_playlistPanelToggleButton is not null)
        {
            _playlistPanelToggleButton.Content = snapshot.ShowPlaylistPanel ? "Hide Playlist" : "Show Playlist";
        }
    }

    private void ApplyLibrarySnapshot(ShellLibrarySnapshot snapshot)
    {
        _currentLibrarySnapshot = snapshot;
        _libraryEntries.Clear();
        foreach (var root in snapshot.Roots)
        {
            AddLibraryEntry(root, 0);
        }
    }

    private void AddLibraryEntry(LibraryEntrySnapshot snapshot, int depth)
    {
        _libraryEntries.Add(LibraryBrowserEntryViewModel.FromSnapshot(snapshot, depth));
        if (!snapshot.IsExpanded)
        {
            return;
        }

        foreach (var child in snapshot.Children)
        {
            AddLibraryEntry(child, depth + 1);
        }
    }

    private void ApplyQueueSnapshot(ShellQueueSnapshot snapshot)
    {
        _currentQueueSnapshot = snapshot;
        _playlistQueueItems.Clear();
        foreach (var item in snapshot.QueueItems)
        {
            _playlistQueueItems.Add(item);
        }

        if (_playlistSummaryTextBlock is not null)
        {
            _playlistSummaryTextBlock.Text = snapshot.QueueItems.Count == 0
                ? "Queue is empty"
                : $"{snapshot.QueueItems.Count} item(s) up next";
        }

        if (_nowPlayingQueueTextBlock is not null)
        {
            _nowPlayingQueueTextBlock.Text = snapshot.NowPlayingItem?.DisplayName ?? "Nothing is playing.";
        }

        if (_playlistQueueListBox?.SelectedItem is ShellPlaylistItem selected
            && !snapshot.QueueItems.Any(item => string.Equals(item.Path, selected.Path, StringComparison.OrdinalIgnoreCase)))
        {
            _playlistQueueListBox.SelectedItem = null;
        }
    }

    private void BrowserPanelToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        var current = _shell.ShellPreferencesService.Current;
        ApplyPreferencesSnapshot(_shell.ShellPreferencesService.ApplyLayoutChange(new ShellLayoutPreferencesChange(
            !current.ShowBrowserPanel,
            current.ShowPlaylistPanel,
            current.WindowMode)));
    }

    private void PlaylistPanelToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        var current = _shell.ShellPreferencesService.Current;
        ApplyPreferencesSnapshot(_shell.ShellPreferencesService.ApplyLayoutChange(new ShellLayoutPreferencesChange(
            current.ShowBrowserPanel,
            !current.ShowPlaylistPanel,
            current.WindowMode)));
    }

    private async void AddLibraryRootButton_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await _shell.FilePickerService.PickFolderAsync(CancellationToken.None);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var result = _shell.ShellLibraryService.PinRoot(folder);
        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            UpdateStatus(result.StatusMessage);
        }

        if (!result.IsError && !_shell.ShellPreferencesService.Current.ShowBrowserPanel)
        {
            var current = _shell.ShellPreferencesService.Current;
            ApplyPreferencesSnapshot(_shell.ShellPreferencesService.ApplyLayoutChange(new ShellLayoutPreferencesChange(
                true,
                current.ShowPlaylistPanel,
                current.WindowMode)));
        }
    }

    private void LibraryExpandButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not LibraryBrowserEntryViewModel entry)
        {
            return;
        }

        _shell.ShellLibraryService.SetExpanded(entry.Path, !entry.IsExpanded);
    }

    private async void LibraryEntryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not LibraryBrowserEntryViewModel entry)
        {
            return;
        }

        try
        {
            if (entry.IsFolder)
            {
                await ApplyQueueMutationAsync(_shell.QueueCommands.EnqueueFolder(entry.Path, autoplay: false));
                return;
            }

            await RunPlaybackQueueCommandAsync(() => _shell.QueueCommands.PlayNow(entry.Path));
        }
        catch (Exception ex)
        {
            UpdateStatus($"Library action failed: {ex.Message}");
        }
    }

    private async void PlaylistQueueItemButton_Click(object? sender, RoutedEventArgs e)
    {
        await PlayQueueItemFromSenderAsync(sender);
    }

    private async void PlaylistQueueItemButton_DoubleTapped(object? sender, TappedEventArgs e)
    {
        await PlayQueueItemFromSenderAsync(sender);
        e.Handled = true;
    }

    private void ClearPlaylistButton_Click(object? sender, RoutedEventArgs e)
    {
        _shell.QueueCommands.ClearQueue();
        UpdateStatus("Queue cleared.");
    }

    private void PlaylistRemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.DataContext is ShellPlaylistItem item)
        {
            RemoveQueueItem(item.Path);
        }
    }

    private void PlaylistRemoveMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.CommandParameter is ShellPlaylistItem item)
        {
            RemoveQueueItem(item.Path);
        }
    }

    private async void PlaylistQueueListBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_playlistQueueListBox?.SelectedItem is not ShellPlaylistItem item)
        {
            return;
        }

        if (e.Key == Key.Delete)
        {
            RemoveQueueItem(item.Path);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            await PlayQueueItemAsync(item);
            e.Handled = true;
        }
    }

    private void RemoveQueueItem(string path)
    {
        var index = _currentQueueSnapshot.QueueItems
            .Select((item, idx) => (item, idx))
            .FirstOrDefault(entry => string.Equals(entry.item.Path, path, StringComparison.OrdinalIgnoreCase))
            .idx;

        if (index < 0 || index >= _currentQueueSnapshot.QueueItems.Count)
        {
            return;
        }

        _shell.QueueCommands.RemoveQueueItemAt(index);
        UpdateStatus($"Removed {Path.GetFileName(path)} from the queue.");
    }

    private async void PlaylistDragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || sender is not Control control
            || control.DataContext is not ShellPlaylistItem item)
        {
            return;
        }

        _draggedQueuePath = item.Path;
        _draggedQueueItem = item;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(PlaylistDragFormat, item.Path));
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            _draggedQueuePath = null;
            _draggedQueueItem = null;
        }

        e.Handled = true;
    }

    private async void PlaylistQueueListBox_DragOver(object? sender, DragEventArgs e)
    {
        var sourcePath = await GetDraggedQueuePathAsync(e);
        e.DragEffects = string.IsNullOrWhiteSpace(sourcePath) ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    private async void PlaylistQueueListBox_Drop(object? sender, DragEventArgs e)
    {
        if (_draggedQueueItem is null)
        {
            return;
        }

        var sourceIndex = FindQueueItemIndex(_draggedQueueItem);
        if (sourceIndex < 0)
        {
            return;
        }

        var targetItem = FindQueueItemFromEventSource(e.Source);
        var targetIndex = targetItem is null ? null : FindQueueItemIndex(targetItem);
        await ReorderQueueAsync(sourceIndex, targetIndex);
        e.Handled = true;
    }

    private Task<string?> GetDraggedQueuePathAsync(DragEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_draggedQueuePath))
        {
            return Task.FromResult<string?>(_draggedQueuePath);
        }

        return Task.FromResult(e.DataTransfer.TryGetValue(PlaylistDragFormat));
    }

    private async Task ReorderQueueAsync(int sourceIndex, int? targetIndex)
    {
        var result = _shell.QueueCommands.ReorderQueueItem(sourceIndex, targetIndex);
        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            UpdateStatus(result.StatusMessage);
        }

        await Task.CompletedTask;
    }

    private int FindQueueItemIndex(ShellPlaylistItem item)
    {
        for (var index = 0; index < _playlistQueueItems.Count; index++)
        {
            if (ReferenceEquals(_playlistQueueItems[index], item))
            {
                return index;
            }
        }

        for (var index = 0; index < _playlistQueueItems.Count; index++)
        {
            var candidate = _playlistQueueItems[index];
            if (string.Equals(candidate.Path, item.Path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.DisplayName, item.DisplayName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static ShellPlaylistItem? FindQueueItemFromEventSource(object? source)
    {
        var current = source as Visual;
        while (current is not null)
        {
            if (current is StyledElement styledElement && styledElement.DataContext is ShellPlaylistItem item)
            {
                return item;
            }

            current = current.GetVisualParent();
        }

        return null;
    }

    private async Task PlayQueueItemFromSenderAsync(object? sender)
    {
        if (sender is not Control control || control.DataContext is not ShellPlaylistItem item)
        {
            return;
        }

        await PlayQueueItemAsync(item);
    }

    private async Task PlayQueueItemAsync(ShellPlaylistItem item)
    {
        if (_playlistQueueListBox is not null)
        {
            _playlistQueueListBox.SelectedItem = item;
        }

        await RunPlaybackQueueCommandAsync(() => _shell.QueueCommands.PlayNow(item.Path));
    }

    private async Task RunPlaybackQueueCommandAsync(Func<ShellQueueMediaResult> command)
    {
        if (!_backendInitialized)
        {
            UpdateStatus("Playback surface is not ready yet.");
            return;
        }

        try
        {
            await ApplyQueueMutationAsync(command());
        }
        catch (Exception ex)
        {
            UpdateStatus($"Playback failed: {ex.Message}");
        }
    }

    private async Task ApplyQueueMutationAsync(ShellQueueMediaResult result)
    {
        foreach (var folder in result.PinnedFolders)
        {
            _shell.ShellLibraryService.PinRoot(folder);
        }

        if (result.UpdatedPreferences is not null)
        {
            ApplyPreferencesSnapshot(result.UpdatedPreferences);
        }

        if (result.RevealBrowserPane && !_shell.ShellPreferencesService.Current.ShowBrowserPanel)
        {
            var current = _shell.ShellPreferencesService.Current;
            ApplyPreferencesSnapshot(_shell.ShellPreferencesService.ApplyLayoutChange(new ShellLayoutPreferencesChange(
                true,
                current.ShowPlaylistPanel,
                current.WindowMode)));
        }

        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            UpdateStatus(result.StatusMessage);
        }

        if (result.ItemToLoad is not null)
        {
            await OpenQueueItemAsync(result.ItemToLoad, result.StatusMessage ?? $"Now playing {result.ItemToLoad.DisplayName}.");
        }
    }
}
