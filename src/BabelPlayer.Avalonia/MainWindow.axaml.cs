using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public partial class MainWindow : Window
{
    private readonly SubtitleOverlayWindow _subtitleOverlay;
    private readonly AvaloniaShellDependencies _shell;
    private readonly Task _subtitleWorkflowInitializationTask;
    private Border? _videoSurfaceBorder;
    private MpvNativeHost? _videoHost;
    private Button? _playPauseButton;
    private TextBlock? _positionTextBlock;
    private TextBlock? _statusTextBlock;
    private ProgressBar? _timelineProgressBar;
    private Slider? _volumeSlider;
    private bool _backendInitialized;
    private bool _updatingVolumeFromProjection;
    private string? _currentMediaPath;

    public MainWindow()
    {
        InitializeComponent();

        _shell = new AvaloniaShellCompositionRoot().Create(this);
        _subtitleOverlay = new SubtitleOverlayWindow();
        _subtitleWorkflowInitializationTask = _shell.SubtitleWorkflowService.InitializeAsync();

        Opened += HandleOpened;
        PositionChanged += HandleOverlayPositionInvalidated;
        Resized += HandleOverlayPositionInvalidated;
        ScalingChanged += HandleOverlayPositionInvalidated;
        Closed += HandleClosed;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _videoSurfaceBorder ??= this.FindControl<Border>("VideoSurfaceBorder");
        _videoHost ??= this.FindControl<MpvNativeHost>("VideoHost");
        _playPauseButton ??= this.FindControl<Button>("PlayPauseButton");
        _positionTextBlock ??= this.FindControl<TextBlock>("PositionTextBlock");
        _statusTextBlock ??= this.FindControl<TextBlock>("StatusTextBlock");
        _timelineProgressBar ??= this.FindControl<ProgressBar>("TimelineProgressBar");
        _volumeSlider ??= this.FindControl<Slider>("VolumeSlider");

        if (_videoSurfaceBorder is not null)
        {
            _videoSurfaceBorder.LayoutUpdated -= HandleVideoSurfaceLayoutUpdated;
            _videoSurfaceBorder.LayoutUpdated += HandleVideoSurfaceLayoutUpdated;
        }

        if (_videoHost is not null)
        {
            _videoHost.HostHandleReady -= HandleHostHandleReady;
            _videoHost.HostHandleReady += HandleHostHandleReady;
        }

        if (_volumeSlider is not null)
        {
            _volumeSlider.PropertyChanged -= HandleVolumeSliderPropertyChanged;
            _volumeSlider.PropertyChanged += HandleVolumeSliderPropertyChanged;
        }

        _shell.PlaybackHostRuntime.MediaOpened -= HandleMediaOpened;
        _shell.PlaybackHostRuntime.MediaOpened += HandleMediaOpened;
        _shell.PlaybackHostRuntime.MediaEnded -= HandleMediaEnded;
        _shell.PlaybackHostRuntime.MediaEnded += HandleMediaEnded;
        _shell.PlaybackHostRuntime.MediaFailed -= HandleMediaFailed;
        _shell.PlaybackHostRuntime.MediaFailed += HandleMediaFailed;

        _shell.ShellProjectionReader.ProjectionChanged -= HandleProjectionChanged;
        _shell.ShellProjectionReader.ProjectionChanged += HandleProjectionChanged;
        _shell.SubtitleWorkflowService.StatusChanged -= HandleSubtitleWorkflowStatusChanged;
        _shell.SubtitleWorkflowService.StatusChanged += HandleSubtitleWorkflowStatusChanged;
        _shell.SubtitleWorkflowService.SnapshotChanged -= HandleSubtitleWorkflowSnapshotChanged;
        _shell.SubtitleWorkflowService.SnapshotChanged += HandleSubtitleWorkflowSnapshotChanged;

        _subtitleOverlay.ApplyStyle(_shell.ShellPreferencesService.Current.SubtitleStyle);
        ApplyProjection(_shell.ShellProjectionReader.Current);
        ApplySubtitleOverlay(_shell.SubtitleWorkflowService.Current);
    }

    private void HandleOpened(object? sender, EventArgs e)
    {
        if (!_subtitleOverlay.IsVisible)
        {
            _subtitleOverlay.Show(this);
        }

        SyncSubtitleOverlay();
    }

    private void HandleClosed(object? sender, EventArgs e)
    {
        if (_videoSurfaceBorder is not null)
        {
            _videoSurfaceBorder.LayoutUpdated -= HandleVideoSurfaceLayoutUpdated;
        }

        if (_videoHost is not null)
        {
            _videoHost.HostHandleReady -= HandleHostHandleReady;
        }

        if (_volumeSlider is not null)
        {
            _volumeSlider.PropertyChanged -= HandleVolumeSliderPropertyChanged;
        }

        _shell.PlaybackHostRuntime.MediaOpened -= HandleMediaOpened;
        _shell.PlaybackHostRuntime.MediaEnded -= HandleMediaEnded;
        _shell.PlaybackHostRuntime.MediaFailed -= HandleMediaFailed;
        _shell.ShellProjectionReader.ProjectionChanged -= HandleProjectionChanged;
        _shell.SubtitleWorkflowService.StatusChanged -= HandleSubtitleWorkflowStatusChanged;
        _shell.SubtitleWorkflowService.SnapshotChanged -= HandleSubtitleWorkflowSnapshotChanged;
        _shell.Dispose();

        if (_subtitleOverlay.IsVisible)
        {
            _subtitleOverlay.Close();
        }
    }

    private void HandleHostHandleReady(nint hostHandle)
    {
        _ = InitializePlaybackAsync(hostHandle);
    }

    private async Task InitializePlaybackAsync(nint hostHandle)
    {
        if (_backendInitialized)
        {
            return;
        }

        try
        {
            await _shell.PlaybackHostRuntime.InitializeAsync(hostHandle, CancellationToken.None);
            _backendInitialized = true;
            UpdateStatus("Playback surface ready.");
        }
        catch (Exception ex)
        {
            UpdateStatus($"Failed to initialize libmpv: {ex.Message}");
        }
    }

    private void HandleVideoSurfaceLayoutUpdated(object? sender, EventArgs e)
    {
        SyncSubtitleOverlay();
    }

    private void HandleOverlayPositionInvalidated(object? sender, EventArgs e)
    {
        SyncSubtitleOverlay();
    }

    private void HandleProjectionChanged(ShellProjectionSnapshot projection)
    {
        Dispatcher.UIThread.Post(() => ApplyProjection(projection));
    }

    private void HandleSubtitleWorkflowStatusChanged(string message)
    {
        Dispatcher.UIThread.Post(() => UpdateStatus(message));
    }

    private void HandleSubtitleWorkflowSnapshotChanged(SubtitleWorkflowSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            ApplySubtitleOverlay(snapshot);
            await ApplyCaptionStartupGateAsync(snapshot);
        });
    }

    private void HandleMediaOpened(ShellPlaybackStateSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var result = await _shell.ShellPlaybackCommands.HandleMediaOpenedAsync(
                snapshot,
                _shell.ShellPreferencesService.Current,
                CancellationToken.None);
            UpdateStatus(result.ResumePosition is TimeSpan
                ? $"Resumed {Path.GetFileName(snapshot.Path)}"
                : result.StatusMessage);
        });
    }

    private void HandleMediaEnded(ShellPlaybackStateSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var result = _shell.ShellPlaybackCommands.HandleMediaEnded(_shell.ShellPreferencesService.Current.ResumeEnabled);
            if (result.NextItem is null)
            {
                UpdateStatus(result.StatusMessage);
                return;
            }

            await LoadPlaybackItemAsync(result.NextItem, CancellationToken.None);
        });
    }

    private void HandleMediaFailed(string message)
    {
        Dispatcher.UIThread.Post(() => UpdateStatus(message));
    }

    private void ApplyProjection(ShellProjectionSnapshot projection)
    {
        var transport = projection.Transport;

        if (_playPauseButton is not null)
        {
            _playPauseButton.Content = transport.IsPaused ? "Play" : "Pause";
            _playPauseButton.IsEnabled = !string.IsNullOrWhiteSpace(transport.Path);
        }

        if (_positionTextBlock is not null)
        {
            _positionTextBlock.Text = $"{transport.CurrentTimeText} / {transport.DurationText}";
        }

        if (_timelineProgressBar is not null)
        {
            _timelineProgressBar.Maximum = Math.Max(transport.DurationSeconds, 1);
            _timelineProgressBar.Value = Math.Clamp(transport.PositionSeconds, 0, _timelineProgressBar.Maximum);
        }

        if (_volumeSlider is not null)
        {
            _updatingVolumeFromProjection = true;
            _volumeSlider.Value = transport.Volume;
            _updatingVolumeFromProjection = false;
        }

        if (!string.IsNullOrWhiteSpace(transport.Path)
            && !string.Equals(_currentMediaPath, transport.Path, StringComparison.OrdinalIgnoreCase))
        {
            _currentMediaPath = transport.Path;
            UpdateStatus(Path.GetFileName(transport.Path));
        }
    }

    private async void OpenButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await _shell.FilePickerService.PickMediaFilesAsync();
            var path = files.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await _subtitleWorkflowInitializationTask;

            if (_videoHost?.HostHandle is > 0 && !_backendInitialized)
            {
                await InitializePlaybackAsync(_videoHost.HostHandle);
            }

            if (!_backendInitialized)
            {
                UpdateStatus("Playback surface is not ready yet.");
                return;
            }

            var queueResult = _shell.QueueCommands.PlayNow(path);
            var loaded = await LoadPlaybackItemAsync(queueResult.ItemToLoad, CancellationToken.None);
            if (loaded)
            {
                UpdateStatus(queueResult.StatusMessage ?? $"Opening {Path.GetFileName(path)}");
            }
        }
        catch (Exception ex)
        {
            UpdateStatus($"Open failed: {ex.Message}");
        }
    }

    private async void PlayPauseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_backendInitialized)
        {
            return;
        }

        var transport = _shell.ShellProjectionReader.Current.Transport;
        if (transport.IsPaused)
        {
            await _shell.ShellPlaybackCommands.PlayAsync(CancellationToken.None);
        }
        else
        {
            await _shell.ShellPlaybackCommands.PauseAsync(CancellationToken.None);
        }
    }

    private async void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_backendInitialized)
        {
            await _shell.PlaybackHostRuntime.StopAsync(CancellationToken.None);
        }
    }

    private async void SeekBackButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_backendInitialized)
        {
            await _shell.ShellPlaybackCommands.SeekRelativeAsync(TimeSpan.FromSeconds(-10), CancellationToken.None);
        }
    }

    private async void SeekForwardButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_backendInitialized)
        {
            await _shell.ShellPlaybackCommands.SeekRelativeAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    private void HandleVolumeSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_updatingVolumeFromProjection || e.Property.Name != nameof(Slider.Value) || !_backendInitialized || sender is not Slider slider)
        {
            return;
        }

        _ = _shell.ShellPlaybackCommands.ApplyAudioPreferencesAsync(
            slider.Value,
            _shell.ShellProjectionReader.Current.Transport.IsMuted,
            CancellationToken.None);
    }

    private void SyncSubtitleOverlay()
    {
        if (_videoSurfaceBorder is null || !_subtitleOverlay.IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(_videoSurfaceBorder);
        if (topLevel is null || _videoSurfaceBorder.Bounds.Width <= 0 || _videoSurfaceBorder.Bounds.Height <= 0)
        {
            return;
        }

        var screenOrigin = _videoSurfaceBorder.PointToScreen(new Point(0, 0));
        var size = _videoSurfaceBorder.Bounds.Size;

        _subtitleOverlay.Position = screenOrigin;
        _subtitleOverlay.Width = size.Width;
        _subtitleOverlay.Height = size.Height;
        _subtitleOverlay.InvalidateMeasure();
    }

    private void ApplySubtitleOverlay(SubtitleWorkflowSnapshot snapshot)
    {
        _subtitleOverlay.ApplyStyle(_shell.ShellPreferencesService.Current.SubtitleStyle);
        _subtitleOverlay.ApplyPresentation(_shell.SubtitleWorkflowService.GetOverlayPresentation(
            _shell.ShellPreferencesService.Current.SubtitleRenderMode,
            subtitlesVisible: _shell.ShellPreferencesService.Current.SubtitleRenderMode != ShellSubtitleRenderMode.Off));
        SyncSubtitleOverlay();
    }

    private ShellLoadMediaOptions BuildLoadOptions()
    {
        var preferences = _shell.ShellPreferencesService.Current;
        return new ShellLoadMediaOptions
        {
            HardwareDecodingMode = preferences.HardwareDecodingMode,
            PlaybackRate = preferences.PlaybackRate,
            AspectRatio = preferences.AspectRatio,
            AudioDelaySeconds = preferences.AudioDelaySeconds,
            SubtitleDelaySeconds = preferences.SubtitleDelaySeconds,
            Volume = preferences.VolumeLevel,
            IsMuted = preferences.IsMuted,
            ResumeEnabled = preferences.ResumeEnabled,
            PreviousPlaybackState = _shell.ShellPlaybackCommands.CurrentPlaybackSnapshot
        };
    }

    private Task<bool> LoadPlaybackItemAsync(ShellPlaylistItem? item, CancellationToken cancellationToken)
    {
        return _shell.ShellPlaybackCommands.LoadPlaybackItemAsync(
            item,
            BuildLoadOptions(),
            cancellationToken);
    }

    private async Task ApplyCaptionStartupGateAsync(SubtitleWorkflowSnapshot snapshot)
    {
        if (_shell.ShellPreferencesService.Current.SubtitleRenderMode == ShellSubtitleRenderMode.Off)
        {
            return;
        }

        var result = await _shell.ShellPlaybackCommands.EvaluateCaptionStartupGateAsync(
            snapshot,
            _shell.ShellPlaybackCommands.CurrentPlaybackSnapshot,
            CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            UpdateStatus(result.StatusMessage);
        }
    }

    private void UpdateStatus(string message)
    {
        if (_statusTextBlock is not null)
        {
            _statusTextBlock.Text = message;
        }
    }
}
