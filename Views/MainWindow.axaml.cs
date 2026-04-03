using System;
using System.IO;
using System.Text;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.ViewModels;

namespace Babel.Player.Views;

public partial class MainWindow : Window
{
    private LibMpvEmbeddedTransport? _embeddedTransport;

    // Cached handler refs so we can unsubscribe in OnClosed.
    private PropertyChangedEventHandler? _playbackPropertyChangedHandler;
    private PropertyChangedEventHandler? _coordinatorPropertyChangedHandler;
    private EventHandler<PointerEventArgs>? _videoOverlayPointerMovedHandler;
    private EventHandler<SizeChangedEventArgs>? _videoViewSizeChangedHandler;
    private EventHandler? _videoNativePointerActivityHandler;

    public MainWindow()
    {
        InitializeComponent();

        var videoView = this.FindControl<MpvVideoView>("VideoView");
        if (videoView is not null)
        {
            videoView.HandleReady += OnVideoHandleReady;
            _videoViewSizeChangedHandler = OnVideoViewSizeChanged;
            videoView.SizeChanged += _videoViewSizeChangedHandler;
            _videoNativePointerActivityHandler = OnVideoNativePointerActivity;
            videoView.NativePointerActivity += _videoNativePointerActivityHandler;
        }
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

        var videoView = this.FindControl<MpvVideoView>("VideoView");
        if (videoView is not null)
        {
            videoView.HandleReady -= OnVideoHandleReady;
            if (_videoViewSizeChangedHandler is not null)
                videoView.SizeChanged -= _videoViewSizeChangedHandler;
            if (_videoNativePointerActivityHandler is not null)
                videoView.NativePointerActivity -= _videoNativePointerActivityHandler;
        }

        _playbackPropertyChangedHandler = null;
        _coordinatorPropertyChangedHandler = null;
        _videoOverlayPointerMovedHandler = null;
        _videoViewSizeChangedHandler = null;
        _videoNativePointerActivityHandler = null;
    }

    private void OnVideoAreaPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Playback.NotifyControlsActivity();
    }

    private void OnVideoNativePointerActivity(object? sender, EventArgs e)
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
                    if (sender is MpvVideoView videoView)
                        UpdateEmbeddedTransportDisplaySize(videoView);
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

    private void OnVideoViewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is MpvVideoView videoView)
            UpdateEmbeddedTransportDisplaySize(videoView);
    }

    private void UpdateEmbeddedTransportDisplaySize(MpvVideoView videoView)
    {
        if (_embeddedTransport is null)
            return;

        var scale = TopLevel.GetTopLevel(videoView)?.RenderScaling ?? 1.0;
        var width = Math.Max(0, (int)Math.Round(videoView.Bounds.Width * scale));
        var height = Math.Max(0, (int)Math.Round(videoView.Bounds.Height * scale));
        _embeddedTransport.SetDisplaySize(width, height);
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

    public void OnAutoSpeakerSetupGuideClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Open both model gate pages the user needs to accept.
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://huggingface.co/pyannote/speaker-diarization-3.1",
                UseShellExecute = true,
            });

            Process.Start(new ProcessStartInfo
            {
                FileName = "https://huggingface.co/pyannote/segmentation-3.0",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.Playback.StatusText = $"Failed to open setup guide: {ex.Message}";
        }
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
