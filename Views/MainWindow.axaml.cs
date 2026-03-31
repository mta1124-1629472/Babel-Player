using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.ViewModels;

namespace Babel.Player.Views;

public partial class MainWindow : Window
{
    private LibMpvEmbeddedTransport? _embeddedTransport;

    public MainWindow()
    {
        InitializeComponent();

        var videoView = this.FindControl<MpvVideoView>("VideoView");
        if (videoView is not null)
        {
            videoView.HandleReady += OnVideoHandleReady;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Playback.PropertyChanged += OnPlaybackPropertyChanged;

            // Subscribe to PointerMoved on the transparent overlay Panel that sits above the
            // NativeControlHost (MpvVideoView). The native Win32 HWND does not bubble Avalonia
            // pointer events to parent controls, so we capture on the managed overlay instead.
            var overlay = this.FindControl<Panel>("VideoOverlayPanel");
            if (overlay is not null)
                overlay.PointerMoved += OnVideoAreaPointerMoved;
        }
    }

    private void OnVideoAreaPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Playback.NotifyControlsActivity();
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EmbeddedPlaybackViewModel.SelectedSegment):
                var item = (sender as EmbeddedPlaybackViewModel)?.SelectedSegment;
                if (item != null)
                    this.FindControl<ListBox>("SegmentList")?.ScrollIntoView(item);
                break;
            case nameof(EmbeddedPlaybackViewModel.IsFullscreen):
                if (DataContext is MainWindowViewModel vm)
                    WindowState = vm.Playback.IsFullscreen ? WindowState.FullScreen : WindowState.Normal;
                break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel vm && vm.Playback.IsFullscreen)
        {
            vm.Playback.IsFullscreen = false;
            e.Handled = true;
        }
    }

    private void OnVideoHandleReady(object? sender, IntPtr hwnd)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            try
            {
                var player = vm.Coordinator.GetOrCreateSourcePlayer();
                if (player is LibMpvEmbeddedTransport embedded)
                {
                    _embeddedTransport = embedded;
                    embedded.AttachToWindow(hwnd);
                    LoadMediaIntoPlayer();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Video init error: {ex.Message}");
            }
        }
    }

    public async void OnOpenMediaClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Media File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.webm", "*.mov" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } },
            }
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        if (DataContext is not MainWindowViewModel vm) return;

        try
        {
            vm.Coordinator.LoadMedia(path);

            if (_embeddedTransport is not null)
            {
                LoadMediaIntoPlayer();
                _embeddedTransport.Play();
            }
        }
        catch (Exception ex)
        {
            vm.Playback.StatusText = $"Failed to open: {ex.Message}";
        }
    }

    public void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var (service, current) = vm.GetSettingsContext();
        var settingsVm = new SettingsViewModel(service, vm.Coordinator, this);
        var win = new SettingsWindow { DataContext = settingsVm };
        _ = win.ShowDialog(this);
    }

    public void OnRecentSessionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is RecentSessionEntry entry
            && DataContext is MainWindowViewModel vm)
        {
            vm.Coordinator.RestoreSession(entry.SessionId);
            cb.SelectedItem = null;   // act as a menu, not a persistent selection
            LoadMediaIntoPlayer();
        }
    }

    /// <summary>
    /// Loads the current session's ingested media into the embedded transport (paused)
    /// and re-applies any active subtitle track (mpv clears tracks on file load).
    /// Called after Open Media, video handle ready, and RestoreSession.
    /// </summary>
    private void LoadMediaIntoPlayer()
    {
        if (DataContext is not MainWindowViewModel vm || _embeddedTransport is null) return;
        var ingestedPath = vm.Coordinator.CurrentSession.IngestedMediaPath;
        if (!string.IsNullOrEmpty(ingestedPath) && System.IO.File.Exists(ingestedPath))
        {
            _embeddedTransport.Load(ingestedPath);
            vm.Playback.ReapplySubtitlesIfActive();
        }
    }
}