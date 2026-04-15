using System;
using System.IO;
using System.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.ViewModels;

namespace Babel.Player.Views;

public partial class MainWindow : Window
{
    private const double ControlsActivityDebounceMs = 75;
    private LibMpvEmbeddedTransport? _embeddedTransport;

    private PropertyChangedEventHandler? _playbackPropertyChangedHandler;
    private PropertyChangedEventHandler? _coordinatorPropertyChangedHandler;
    private EventHandler<PointerEventArgs>? _videoOverlayPointerMovedHandler;
    private EventHandler<SizeChangedEventArgs>? _videoViewSizeChangedHandler;
    private EventHandler? _videoNativePointerActivityHandler;
    private EventHandler<PixelPointEventArgs>? _windowPositionChangedHandler;
    private EventHandler? _windowScalingChangedHandler;
    private EventHandler? _screensChangedHandler;
    private Screens? _subscribedScreens;
    private long _lastControlsActivityTickMs;
    private bool _isApplyingWindowStateFromViewModel;
    private bool _isApplyingFullscreenFromWindowState;

    public MainWindow()
    {
        InitializeComponent();

        // Keep the title in sync with the build configuration.
        Title = AppIdentity.AppName;

#if BABEL_DEV
        var devLogButton = this.FindControl<Button>("DevLogButton");
        if (devLogButton is not null)
            devLogButton.Click += OnDevLogClick;
        var freshStartButton = this.FindControl<Button>("FreshStartButton");
        if (freshStartButton is not null)
            freshStartButton.Click += OnFreshStartClick;
#endif

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
            vm.Playback.Preview.PropertyChanged += _playbackPropertyChangedHandler;
            _coordinatorPropertyChangedHandler = OnCoordinatorPropertyChanged;
            vm.Coordinator.PropertyChanged += _coordinatorPropertyChangedHandler;

            var overlay = this.FindControl<Panel>("VideoOverlayPanel");
            if (overlay is not null)
            {
                _videoOverlayPointerMovedHandler = OnVideoAreaPointerMoved;
                overlay.PointerMoved += _videoOverlayPointerMovedHandler;
            }
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        _windowPositionChangedHandler ??= OnWindowPositionChanged;
        _windowScalingChangedHandler ??= OnWindowMetricsChanged;
        _screensChangedHandler ??= OnScreensChanged;

        PositionChanged += _windowPositionChangedHandler;
        ScalingChanged += _windowScalingChangedHandler;

        _subscribedScreens = Screens;
        _subscribedScreens.Changed += _screensChangedHandler;

        var videoView = this.FindControl<MpvVideoView>("VideoView");
        if (videoView is not null)
            UpdateEmbeddedTransportViewportMetrics(videoView);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != WindowStateProperty || _isApplyingWindowStateFromViewModel)
            return;

        if (DataContext is not MainWindowViewModel vm)
            return;

        var isFullscreen = WindowState == WindowState.FullScreen;
        if (vm.Playback.Preview.IsFullscreen == isFullscreen)
            return;

        _isApplyingFullscreenFromWindowState = true;
        vm.Playback.Preview.IsFullscreen = isFullscreen;
        _isApplyingFullscreenFromWindowState = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (_windowPositionChangedHandler is not null)
        {
            PositionChanged -= _windowPositionChangedHandler;
        }

        if (_windowScalingChangedHandler is not null)
        {
            ScalingChanged -= _windowScalingChangedHandler;
        }

        if (_subscribedScreens is not null && _screensChangedHandler is not null)
            _subscribedScreens.Changed -= _screensChangedHandler;

        if (DataContext is MainWindowViewModel vm)
        {
            if (_playbackPropertyChangedHandler is not null)
                vm.Playback.Preview.PropertyChanged -= _playbackPropertyChangedHandler;
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
        _windowPositionChangedHandler = null;
        _windowScalingChangedHandler = null;
        _screensChangedHandler = null;
        _subscribedScreens = null;
    }

    private void OnVideoAreaPointerMoved(object? sender, PointerEventArgs e)
    {
        NotifyControlsActivityDebounced();
    }

    private void OnWindowSurfacePointerMoved(object? sender, PointerEventArgs e)
    {
        NotifyControlsActivityDebounced();
    }

    private void OnVideoNativePointerActivity(object? sender, EventArgs e)
    {
        NotifyControlsActivityDebounced();
    }

    private void NotifyControlsActivityDebounced()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var nowMs = Environment.TickCount64;
        if (nowMs - _lastControlsActivityTickMs < ControlsActivityDebounceMs)
            return;

        _lastControlsActivityTickMs = nowMs;
        vm.Playback.Preview.NotifyControlsActivity();
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EmbeddedPlaybackPreviewViewModel.SelectedSegment):
                var item = (sender as EmbeddedPlaybackPreviewViewModel)?.SelectedSegment;
                if (item != null)
                    this.FindControl<ListBox>("SegmentList")?.ScrollIntoView(item);
                break;
            case nameof(EmbeddedPlaybackPreviewViewModel.IsFullscreen):
                if (DataContext is MainWindowViewModel vm && !_isApplyingFullscreenFromWindowState)
                {
                    var desiredState = vm.Playback.Preview.IsFullscreen ? WindowState.FullScreen : WindowState.Normal;
                    if (WindowState != desiredState)
                    {
                        _isApplyingWindowStateFromViewModel = true;
                        WindowState = desiredState;
                        _isApplyingWindowStateFromViewModel = false;
                    }
                }
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
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel vm && vm.Playback.Preview.IsFullscreen)
        {
            vm.Playback.Preview.IsFullscreen = false;
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
                    embedded.AttachToWindow(hwnd);
                    if (sender is MpvVideoView videoView)
                        UpdateEmbeddedTransportViewportMetrics(videoView);
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
            UpdateEmbeddedTransportViewportMetrics(videoView);
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        OnWindowMetricsChanged(sender, EventArgs.Empty);
    }

    private void OnWindowMetricsChanged(object? sender, EventArgs e)
    {
        var videoView = this.FindControl<MpvVideoView>("VideoView");
        if (videoView is not null)
            UpdateEmbeddedTransportViewportMetrics(videoView);
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        var videoView = this.FindControl<MpvVideoView>("VideoView");
        if (videoView is not null)
            UpdateEmbeddedTransportViewportMetrics(videoView);
    }

    private void UpdateEmbeddedTransportViewportMetrics(MpvVideoView videoView)
    {
        if (_embeddedTransport is null)
            return;

        var scale = TopLevel.GetTopLevel(videoView)?.RenderScaling ?? 1.0;
        var width = Math.Max(0, (int)Math.Round(videoView.Bounds.Width * scale));
        var height = Math.Max(0, (int)Math.Round(videoView.Bounds.Height * scale));
        _embeddedTransport.SetDisplaySize(width, height);

        var screen = Screens.ScreenFromWindow(this);
        _embeddedTransport.SetMonitorResolution(screen?.Bounds.Width ?? 0, screen?.Bounds.Height ?? 0);
    }

    private static readonly string[] VideoExtensions = ["*.mp4", "*.mkv", "*.avi", "*.webm", "*.mov"];
    private static readonly string[] AudioExtensions = ["*.wav", "*.mp3", "*.flac", "*.ogg", "*.m4a"];
    private static readonly string[] AllFilesPattern = ["*.*"];
    private static readonly string[] SrtPattern = ["*.srt"];

    public async void OnOpenMediaClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Media File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Video Files") { Patterns = VideoExtensions },
                new FilePickerFileType("All Files") { Patterns = AllFilesPattern },
            ]
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

    /// <summary>
    /// Opens a file picker to select an audio reference clip for the currently selected speaker and assigns the chosen file to that speaker.
    /// </summary>
    /// <remarks>
    /// If the window's DataContext is not a MainWindowViewModel or there is no selected speaker id, the method returns without action.
    /// If the user cancels the file picker or the chosen file has no local path, the method returns without assigning a reference clip.
    /// When a valid file is selected, the selected speaker routing entry is updated with that reference clip.
    /// </remarks>
    public async void OnBrowseReferenceClipClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (string.IsNullOrWhiteSpace(vm.Playback.SpeakerRouting.SelectedSpeakerId)) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Select reference clip for {vm.Playback.SpeakerRouting.SelectedSpeakerId}",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio Files") { Patterns = AudioExtensions },
                new FilePickerFileType("All Files") { Patterns = AllFilesPattern },
            ]
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        await vm.Playback.SpeakerRouting.SetReferenceAudioForSelectedSpeakerAsync(path);
    }

    public async void OnReviewSpeakerReferencesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var wizard = new SpeakerReferenceWizardWindow
        {
            DataContext = new SpeakerReferenceWizardViewModel(vm.Playback, vm.Coordinator),
        };

        await wizard.ShowDialog(this);
    }

    /// <summary>
    /// Prompts the user to choose an output .srt file and exports the current playback segments as SubRip subtitles.
    /// </summary>
    /// <remarks>
    /// If no segments are available, sets the playback status to "No segments available to export." 
    /// On success sets the playback status to "Exported captions to {file.Name}." 
    /// On failure sets the playback status to "Failed to export captions: {error message}".
    /// </remarks>
    public async void OnExportCaptionsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (!vm.Playback.Preview.HasSegments)
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
            FileTypeChoices =
            [
                new FilePickerFileType("SubRip Subtitle") { Patterns = SrtPattern },
            ]
        });

        if (file is null)
            return;

        var srt = SrtGenerator.Generate(vm.Playback.Preview.Segments);

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

    public void OnMinimizeWindowClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    public void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is Visual sourceVisual && sourceVisual.FindAncestorOfType<Button>() is not null)
            return;

        if (e.ClickCount == 2)
        {
            OnToggleMaximizeWindowClick(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    public void OnToggleMaximizeWindowClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    public void OnCloseWindowClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    public void OnForceCloseWindowClick(object? sender, RoutedEventArgs e)
    {
        ForceCloseCurrentProcess();
    }

    public async void OnKillAllClick(object? sender, RoutedEventArgs e)
    {
        var confirmed = await ShowKillAllConfirmationDialogAsync();
        if (!confirmed)
            return;

        if (DataContext is MainWindowViewModel vm)
        {
            try
            {
                vm.Coordinator.Dispose();
            }
            catch
            {
                // Best-effort teardown before hard kill.
            }
        }

        try
        {
            await TryDockerComposeDownAsync();
        }
        catch
        {
            // Best-effort teardown before hard kill.
        }

        ForceCloseCurrentProcess();
    }

    private async Task<bool> ShowKillAllConfirmationDialogAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = "Confirm Kill All",
        };

        var killAllButton = new Button
        {
            Content = "Kill All",
            Padding = new Thickness(12, 6),
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(12, 6),
        };

        killAllButton.Click += (_, _) =>
        {
            tcs.TrySetResult(true);
            dialog.Close();
        };
        cancelButton.Click += (_, _) =>
        {
            tcs.TrySetResult(false);
            dialog.Close();
        };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Kill All will stop local inference runtimes (including Docker) and then force-close the app.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "Use this only when normal close cannot recover.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, killAllButton },
                },
            },
        };

        _ = dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private static async Task TryDockerComposeDownAsync()
    {
        var dockerPath = DependencyLocator.FindDocker();
        var composeFilePath = ResolveComposeFilePath();
        if (string.IsNullOrWhiteSpace(dockerPath) || string.IsNullOrWhiteSpace(composeFilePath))
            return;

        var workingDirectory = Path.GetDirectoryName(composeFilePath) ?? AppContext.BaseDirectory;
        var psi = new ProcessStartInfo
        {
            FileName = dockerPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        psi.ArgumentList.Add("compose");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(composeFilePath);
        psi.ArgumentList.Add("down");

        using var process = Process.Start(psi);
        if (process is null)
            return;

        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore timeout kill failures.
            }
        }
    }

    private static string? ResolveComposeFilePath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 6; depth++, current = current.Parent)
        {
            foreach (var candidateName in new[] { "docker-compose.yml", "compose.yml", "compose.yaml" })
            {
                var candidate = Path.Combine(current.FullName, candidateName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static void ForceCloseCurrentProcess()
    {
        try
        {
            Process.GetCurrentProcess().Kill(entireProcessTree: true);
        }
        catch
        {
            Environment.FailFast("Force close requested.");
        }
    }

#if BABEL_DEV
    public void OnDevLogClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var win = new Dev.DevLogWindow 
        { 
            DataContext = new ViewModels.Dev.DevLogViewModel(vm.Coordinator.DevLog, this.Clipboard) 
        };
        win.Show();
    }

    public void OnFreshStartClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        _ = vm.Coordinator.FreshStartAsync();
    }
#endif

    public void OnRecentSessionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is RecentSessionEntry entry
            && DataContext is MainWindowViewModel vm)
        {
            vm.Coordinator.RestoreSession(entry.SessionId);
            cb.SelectedItem = null;
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
        vm.Playback.Preview.ReapplySubtitlesIfActive();
        vm.Playback.Preview.IsSourcePaused = !request.AutoPlay;
        if (request.AutoPlay)
            _embeddedTransport.Play();
    }
}
