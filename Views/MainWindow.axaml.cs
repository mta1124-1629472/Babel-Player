using System;
using System.IO;
using System.Text;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.ViewModels;

namespace Babel.Player.Views;

public partial class MainWindow : Window
{
    private LibMpvEmbeddedTransport? _embeddedTransport;

    // Volume slider hover-reveal state
    private bool _isPointerOverVolumeArea;
    private DispatcherTimer? _volumeHideTimer;
    private const int VolumeHideDelayMs = 3000;

    // Cached handler refs so we can unsubscribe in OnClosed.
    private PropertyChangedEventHandler? _playbackPropertyChangedHandler;
    private PropertyChangedEventHandler? _coordinatorPropertyChangedHandler;
    private EventHandler<PointerEventArgs>? _videoOverlayPointerMovedHandler;

    public MainWindow()
    {
        InitializeComponent();

        var videoView = this.FindControl<MpvVideoView>("VideoView");
        if (videoView is not null)
        {
            videoView.HandleReady += OnVideoHandleReady;
        }

        // Initialise the 3-second hide timer (single-shot; restart on each pointer-enter).
        _volumeHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(VolumeHideDelayMs)
        };
        _volumeHideTimer.Tick += OnVolumeHideTimerTick;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            _playbackPropertyChangedHandler = OnPlaybackPropertyChanged;
            vm.Playback.PropertyChanged += _playbackPropertyChangedHandler;
            _coordinatorPropertyChangedHandler = OnCoordinatorPropertyChanged;
            vm.Coordinator.PropertyChanged += _coordinatorPropertyChangedHandler;

            // Subscribe to PointerMoved on the transparent overlay Panel that sits above the
            // NativeControlHost (MpvVideoView). The native Win32 HWND does not bubble Avalonia
            // pointer events to parent controls, so we capture on the managed overlay instead.
            var overlay = this.FindControl<Panel>("VideoOverlayPanel");
            if (overlay is not null)
            {
                _videoOverlayPointerMovedHandler = OnVideoAreaPointerMoved;
                overlay.PointerMoved += _videoOverlayPointerMovedHandler;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // Stop and discard the volume hide timer so it cannot fire after
        // the control tree is torn down.
        if (_volumeHideTimer is not null)
        {
            _volumeHideTimer.Stop();
            _volumeHideTimer.Tick -= OnVolumeHideTimerTick;
            _volumeHideTimer = null;
        }

        // Unsubscribe event handlers so the window is not kept alive by
        // rooted objects (ViewModel, overlay panel) after it has closed.
        if (DataContext is MainWindowViewModel vm)
        {
            if (_playbackPropertyChangedHandler is not null)
                vm.Playback.PropertyChanged -= _playbackPropertyChangedHandler;
            if (_coordinatorPropertyChangedHandler is not null)
                vm.Coordinator.PropertyChanged -= _coordinatorPropertyChangedHandler;

            var overlay = this.FindControl<Panel>("VideoOverlayPanel");
            if (overlay is not null && _videoOverlayPointerMovedHandler is not null)
                overlay.PointerMoved -= _videoOverlayPointerMovedHandler;
        }

        _playbackPropertyChangedHandler = null;
        _coordinatorPropertyChangedHandler = null;
        _videoOverlayPointerMovedHandler = null;
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

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionWorkflowCoordinator.PendingMediaReloadRequest))
            TryApplyPendingMediaReloadRequest();
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

    // ── Volume slider hover-reveal ─────────────────────────────────────────────

    /// <summary>
    /// Called when the pointer enters EITHER the mute button OR the volume slider popup.
    /// Shows the slider popup and cancels any pending hide timer.
    /// </summary>
    public void OnVolumeAreaPointerEntered(object? sender, PointerEventArgs e)
    {
        _isPointerOverVolumeArea = true;
        _volumeHideTimer?.Stop();

        var popup = this.FindControl<Border>("VolumeSliderPopup");
        if (popup is not null)
            popup.IsVisible = true;
    }

    /// <summary>
    /// Called when the pointer leaves EITHER the mute button OR the volume slider popup.
    /// Starts the 3-second retraction timer; if the pointer re-enters before it fires
    /// the timer is cancelled and the slider stays visible.
    /// </summary>
    public void OnVolumeAreaPointerExited(object? sender, PointerEventArgs e)
    {
        _isPointerOverVolumeArea = false;
        _volumeHideTimer?.Stop();
        _volumeHideTimer?.Start();
    }

    private void OnVolumeHideTimerTick(object? sender, EventArgs e)
    {
        _volumeHideTimer?.Stop();

        // Only hide if the pointer is genuinely outside the whole volume area.
        if (!_isPointerOverVolumeArea)
        {
            var popup = this.FindControl<Border>("VolumeSliderPopup");
            if (popup is not null)
                popup.IsVisible = false;
        }
    }

    // ── Video handle + media loading ───────────────────────────────────────────

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
                    TryApplyPendingMediaReloadRequest();
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
        }
        catch (Exception ex)
        {
            vm.Playback.StatusText = $"Failed to open: {ex.Message}";
        }
    }

    public async void OnBrowseReferenceClipClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (string.IsNullOrWhiteSpace(vm.Playback.SelectedSpeakerId)) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Select reference clip for {vm.Playback.SelectedSpeakerId}",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Audio Files") { Patterns = new[] { "*.wav", "*.mp3", "*.flac", "*.ogg", "*.m4a" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } },
            }
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        await vm.Playback.SetReferenceAudioForSelectedSpeaker(path);
    }

    public async void OnExportCaptionsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (!vm.Playback.HasSegments)
        {
            vm.Playback.StatusText = "No segments available to export.";
            return;
        }

        var sourceMediaPath = vm.Coordinator.CurrentSession.SourceMediaPath;
        var suggestedName = string.IsNullOrWhiteSpace(sourceMediaPath)
            ? "babel-player-captions.srt"
            : $"{Path.GetFileNameWithoutExtension(sourceMediaPath)}.srt";

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Captions",
            DefaultExtension = "srt",
            SuggestedFileName = suggestedName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("SubRip Subtitle") { Patterns = new[] { "*.srt" } },
            }
        });

        if (file is null)
            return;

        var srt = SrtGenerator.Generate(vm.Playback.Segments);

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(srt);
            await writer.FlushAsync();

            vm.Playback.StatusText = $"Exported captions to {file.Name}.";
        }
        catch (Exception ex)
        {
            vm.Playback.StatusText = $"Failed to export captions: {ex.Message}";
        }
    }

    public void OnApiKeysClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var store = vm.Coordinator.KeyStore;
        if (store is null) return;
        var validationService = new ApiKeyValidationService(
            vm.Coordinator.TranscriptionRegistry,
            vm.Coordinator.TranslationRegistry,
            vm.Coordinator.TtsRegistry);
        var dialog = new ApiKeysDialog { DataContext = new ApiKeysViewModel(store, validationService) };
        _ = dialog.ShowDialog(this);
    }

    public void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var win = new SettingsWindow();
        win.DataContext = vm.CreateSettingsViewModel(win);
        _ = win.ShowDialog(this);
    }

    public void OnRecentSessionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is RecentSessionEntry entry
            && DataContext is MainWindowViewModel vm)
        {
            vm.Coordinator.RestoreSession(entry.SessionId);
            cb.SelectedItem = null;   // act as a menu, not a persistent selection
        }
    }

    private void TryApplyPendingMediaReloadRequest()
    {
        if (DataContext is not MainWindowViewModel vm || _embeddedTransport is null)
            return;

        var pending = vm.Coordinator.PendingMediaReloadRequest;
        if (pending is null)
            return;

        var request = vm.Coordinator.ConsumePendingMediaReloadRequest();
        if (request is null || !System.IO.File.Exists(request.IngestedMediaPath))
            return;

        _embeddedTransport.Load(request.IngestedMediaPath);
        vm.Playback.ReapplySubtitlesIfActive();
        vm.Playback.IsSourcePaused = !request.AutoPlay;
        if (request.AutoPlay)
            _embeddedTransport.Play();
    }
}
