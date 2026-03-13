using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using BabelPlayer.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI;

namespace BabelPlayer.WinUI;

public sealed partial class MainWindow
{
    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var files = await _filePickerService.PickMediaFilesAsync();
        await ApplyQueueMutationAsync(_queueCommands.EnqueueFiles(files, autoplay: true));
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        await QueueFolderIntoPlaylistAsync(autoplay: true);
    }

    private async void AddRootFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await _filePickerService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var result = _shellLibraryService.PinRoot(folder);
        if (!result.IsError)
        {
            ViewModel.Browser.IsVisible = true;
            BrowserPaneToggle.IsChecked = true;
            ApplyPreferencesSnapshot(_shellPreferenceCommands.ApplyLayoutChange(new ShellLayoutPreferencesChange(
                true,
                ViewModel.Queue.IsVisible,
                _windowModeService.CurrentMode)));
        }

        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            ShowStatus(result.StatusMessage, result.IsError);
        }
    }

    private async void QueuePlaylistFolder_Click(object sender, RoutedEventArgs e)
    {
        await QueueFolderIntoPlaylistAsync(autoplay: false);
    }

    private async void PlaylistList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistItem item)
        {
            await ApplyQueueMutationAsync(_queueCommands.PlayNow(item.Path));
        }
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.Queue.SelectedQueueItem = PlaylistList.SelectedItem as PlaylistItem;
        UpdateWindowHeader();
    }

    private async void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistItem item)
        {
            await ApplyQueueMutationAsync(_queueCommands.PlayNow(item.Path));
        }
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.Queue.SelectedHistoryItem = HistoryList.SelectedItem as PlaylistItem;
        UpdateWindowHeader();
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedIndex < 0)
        {
            return;
        }

        _queueCommands.RemoveQueueItemAt(PlaylistList.SelectedIndex);
    }

    private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
    {
        _queueCommands.ClearQueue();
        ShowStatus("Queue cleared.");
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (await TryHandleLibraryQueueDropAsync(sender, e.DataView))
        {
            e.Handled = true;
            return;
        }

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.Handled = true;
        var storageItems = await e.DataView.GetStorageItemsAsync();
        List<string> files = [];
        List<string> folders = [];
        foreach (var item in storageItems)
        {
            switch (item)
            {
                case StorageFile file when _shellLibraryService.IsSupportedMediaPath(file.Path):
                    files.Add(file.Path);
                    break;
                case StorageFolder folder:
                    folders.Add(folder.Path);
                    break;
            }
        }

        if (IsPlaylistDropTarget(sender))
        {
            var result = _queueCommands.AddDroppedItemsToQueue(files, folders);
            if (!string.IsNullOrWhiteSpace(result.StatusMessage))
            {
                ShowStatus(result.StatusMessage, result.IsError);
            }

            return;
        }

        await ApplyQueueMutationAsync(_queueCommands.EnqueueDroppedItems(files, folders));
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(LibraryQueueDragFormat))
        {
            e.AcceptedOperation = IsPlaylistDropTarget(sender)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BrowserColumn is null
            || PlaylistColumn is null
            || BrowserPane is null
            || PlaylistPane is null
            || PlayerHost is null
            || SourceSubtitleTextBlock is null
            || TranslatedSubtitleTextBlock is null
            || SubtitleOverlayBorder is null)
        {
            return;
        }

        SyncPaneLayout(e.NewSize.Width);
        ApplyAdaptiveStandardLayout(e.NewSize.Height);
        UpdatePortraitVideoLanguageToolsState();
        if (!string.IsNullOrWhiteSpace(_pendingAutoFitPath))
        {
            TryApplyStandardAutoFit();
        }

        UpdateLanguageToolsResponsiveLayout();
        PlayerHost.RequestHostBoundsSync();
        UpdateSubtitleVisibility();
        _stageCoordinator.HandleStageLayoutChanged();
    }

    private void LibraryTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not LibraryEntrySnapshot node || !node.IsFolder)
        {
            return;
        }
        _shellLibraryService.SetExpanded(node.Path, isExpanded: true);
    }

    private async void LibraryTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not LibraryEntrySnapshot node)
        {
            return;
        }

        _selectedLibraryNode = node;
        if (node.IsFolder)
        {
            if (sender.SelectedNode is not null)
            {
                _shellLibraryService.SetExpanded(node.Path, !sender.SelectedNode.IsExpanded);
            }

            UpdateWindowHeader();
            return;
        }

        await LoadLibraryNodeAsync(node);
    }

    private async void LibraryTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedNode?.Content is not LibraryEntrySnapshot node)
        {
            return;
        }

        _selectedLibraryNode = node;
        UpdateWindowHeader();
        if (node.IsFolder)
        {
            return;
        }
        if (_isLibraryDragOperationInProgress)
        {
            return;
        }

        await LoadLibraryNodeAsync(node);
    }

    private void LibraryTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        var paths = args.Items
            .OfType<LibraryEntrySnapshot>()
            .Where(node => !node.IsFolder && !string.IsNullOrWhiteSpace(node.Path))
            .Select(node => node.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            args.Cancel = true;
            return;
        }

        _isLibraryDragOperationInProgress = true;
        args.Data.RequestedOperation = DataPackageOperation.Copy;
        args.Data.SetData(LibraryQueueDragFormat, string.Join('\n', paths));
    }

    private void LibraryTree_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        _isLibraryDragOperationInProgress = false;
    }

    private async Task LoadLibraryNodeAsync(LibraryEntrySnapshot node)
    {
        if (node.IsFolder || string.IsNullOrWhiteSpace(node.Path))
        {
            return;
        }

        if (string.Equals(_pendingLibraryLoadPath, node.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pendingLibraryLoadPath = node.Path;
        try
        {
            await ApplyQueueMutationAsync(_queueCommands.PlayNow(node.Path));
        }
        finally
        {
            _pendingLibraryLoadPath = null;
        }
    }

    private async Task QueueFolderIntoPlaylistAsync(bool autoplay)
    {
        var folder = await _filePickerService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        await ApplyQueueMutationAsync(_queueCommands.EnqueueFolder(folder, autoplay));
    }

    private async Task ApplyQueueMutationAsync(ShellQueueMediaResult result)
    {
        foreach (var folder in result.PinnedFolders)
        {
            _shellLibraryService.PinRoot(folder);
        }

        if (result.UpdatedPreferences is not null)
        {
            ApplyPreferencesSnapshot(result.UpdatedPreferences);
        }

        if (result.RevealBrowserPane)
        {
            ViewModel.Browser.IsVisible = true;
            BrowserPaneToggle.IsChecked = true;
        }

        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            ShowStatus(result.StatusMessage, result.IsError);
        }

        if (result.ItemToLoad is not null)
        {
            await LoadPlaybackItemAsync(result.ItemToLoad);
        }
    }

    private void ApplyQueueSnapshot(PlaybackQueueSnapshot snapshot)
    {
        ViewModel.Queue.NowPlayingItem = snapshot.NowPlayingItem;
        ViewModel.Queue.QueueItems.Clear();
        foreach (var item in snapshot.QueueItems)
        {
            ViewModel.Queue.QueueItems.Add(item);
        }

        ViewModel.Queue.HistoryItems.Clear();
        foreach (var item in snapshot.HistoryItems)
        {
            ViewModel.Queue.HistoryItems.Add(item);
        }

        if (ViewModel.Queue.SelectedQueueItem is not null
            && !snapshot.QueueItems.Any(item => string.Equals(item.Path, ViewModel.Queue.SelectedQueueItem.Path, StringComparison.OrdinalIgnoreCase)))
        {
            ViewModel.Queue.SelectedQueueItem = null;
            PlaylistList.SelectedItem = null;
        }

        if (ViewModel.Queue.SelectedHistoryItem is not null
            && !snapshot.HistoryItems.Any(item => string.Equals(item.Path, ViewModel.Queue.SelectedHistoryItem.Path, StringComparison.OrdinalIgnoreCase)))
        {
            ViewModel.Queue.SelectedHistoryItem = null;
            HistoryList.SelectedItem = null;
        }

        NowPlayingQueueTextBlock.Text = snapshot.NowPlayingItem?.DisplayName ?? "Nothing is playing.";
        PlaylistSummaryTextBlock.Text = snapshot.QueueItems.Count == 0
            ? "Queue is empty"
            : $"{snapshot.QueueItems.Count} item(s) up next";
        UpdateWindowHeader();
        UpdateQueueDiagnostics(snapshot);
    }

    private void RebuildLibraryTree()
    {
        LibraryTree.RootNodes.Clear();
        foreach (var root in ViewModel.Browser.Roots)
        {
            LibraryTree.RootNodes.Add(CreateTreeNode(root));
        }
    }

    private TreeViewNode CreateTreeNode(LibraryEntrySnapshot model)
    {
        var node = new TreeViewNode
        {
            Content = model,
            IsExpanded = model.IsExpanded,
            HasUnrealizedChildren = model.HasUnrealizedChildren
        };

        if (model.Children.Count > 0)
        {
            node.HasUnrealizedChildren = false;
            foreach (var child in model.Children)
            {
                node.Children.Add(CreateTreeNode(child));
            }
        }

        return node;
    }

    private bool IsPlaylistDropTarget(object sender)
        => ReferenceEquals(sender, PlaylistPane) || ReferenceEquals(sender, PlaylistList);

    private async Task<bool> TryHandleLibraryQueueDropAsync(object sender, DataPackageView dataView)
    {
        if (!IsPlaylistDropTarget(sender) || !dataView.Contains(LibraryQueueDragFormat))
        {
            return false;
        }

        var data = await dataView.GetDataAsync(LibraryQueueDragFormat);
        if (data is not string rawPaths)
        {
            return false;
        }

        var paths = rawPaths
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return true;
        }

        var result = _queueCommands.AddToQueue(paths);
        ShowStatus(result.StatusMessage
            ?? (paths.Length == 1
                ? $"Queued {Path.GetFileName(paths[0])}."
                : $"Queued {paths.Length} item(s) from the library."));
        return true;
    }

    private static bool IsAppStillForeground()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(foregroundWindow, out var processId);
        return processId == Environment.ProcessId;
    }
}

