using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public partial class MainWindow : Window
{
    private readonly AvaloniaShellDependencies _shell;
    private readonly SubtitleOverlayWindow _subtitleOverlay = new();
    private Border? _videoSurfaceBorder;
    private MpvNativeHost? _videoHost;
    private Button? _playPauseButton;
    private TextBlock? _positionTextBlock;
    private TextBlock? _statusTextBlock;
    private Slider? _timelineSlider;
    private Slider? _volumeSlider;
    private bool _backendInitialized;
    private bool _subtitleWorkflowInitialized;
    private bool _startupMediaLoaded;
    private bool _updatingTimelineFromProjection;
    private bool _updatingVolumeFromProjection;
    private string? _currentMediaPath;

    public MainWindow()
    {
        InitializeComponent();
        _shell = new AvaloniaShellCompositionRoot().Create(this);

        Opened += HandleOpened;
        PositionChanged += HandleOverlayPositionInvalidated;
        Resized += HandleOverlayPositionInvalidated;
        ScalingChanged += HandleOverlayPositionInvalidated;
        AddHandler(DragDrop.DragOverEvent, HandleWindowDragOver);
        AddHandler(DragDrop.DropEvent, HandleWindowDrop);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _videoSurfaceBorder ??= this.FindControl<Border>("VideoSurfaceBorder");
        _videoHost ??= this.FindControl<MpvNativeHost>("VideoHost");
        _playPauseButton ??= this.FindControl<Button>("PlayPauseButton");
        _positionTextBlock ??= this.FindControl<TextBlock>("PositionTextBlock");
        _statusTextBlock ??= this.FindControl<TextBlock>("StatusTextBlock");
        _timelineSlider ??= this.FindControl<Slider>("TimelineSlider");
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

        if (_timelineSlider is not null)
        {
            _timelineSlider.PropertyChanged -= HandleTimelineSliderPropertyChanged;
            _timelineSlider.PropertyChanged += HandleTimelineSliderPropertyChanged;
        }

        _shell.ShellProjectionReader.ProjectionChanged -= HandleProjectionChanged;
        _shell.ShellProjectionReader.ProjectionChanged += HandleProjectionChanged;
        _shell.SubtitleWorkflowService.SnapshotChanged -= HandleSubtitleSnapshotChanged;
        _shell.SubtitleWorkflowService.SnapshotChanged += HandleSubtitleSnapshotChanged;
        _shell.SubtitleWorkflowService.StatusChanged -= HandleSubtitleStatusChanged;
        _shell.SubtitleWorkflowService.StatusChanged += HandleSubtitleStatusChanged;
        _shell.PlaybackHostRuntime.MediaOpened -= HandleMediaOpened;
        _shell.PlaybackHostRuntime.MediaOpened += HandleMediaOpened;
        _shell.PlaybackHostRuntime.MediaEnded -= HandleMediaEnded;
        _shell.PlaybackHostRuntime.MediaEnded += HandleMediaEnded;
        _shell.PlaybackHostRuntime.MediaFailed -= HandleMediaFailed;
        _shell.PlaybackHostRuntime.MediaFailed += HandleMediaFailed;

        EnsureSubtitlesVisible();
        ApplyProjection(_shell.ShellProjectionReader.Current);
        ApplySubtitlePresentation(_shell.SubtitleWorkflowService.Current);
        UpdateStatus(File.Exists(GetBundledTestVideoPath())
            ? $"Loading demo clip: {Path.GetFileName(GetBundledTestVideoPath())}"
            : "Open a local video to begin playback.");
        SyncSubtitleOverlay();
    }

    protected override void OnClosed(EventArgs e)
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

        if (_timelineSlider is not null)
        {
            _timelineSlider.PropertyChanged -= HandleTimelineSliderPropertyChanged;
        }

        _shell.ShellProjectionReader.ProjectionChanged -= HandleProjectionChanged;
        _shell.SubtitleWorkflowService.SnapshotChanged -= HandleSubtitleSnapshotChanged;
        _shell.SubtitleWorkflowService.StatusChanged -= HandleSubtitleStatusChanged;
        _shell.PlaybackHostRuntime.MediaOpened -= HandleMediaOpened;
        _shell.PlaybackHostRuntime.MediaEnded -= HandleMediaEnded;
        _shell.PlaybackHostRuntime.MediaFailed -= HandleMediaFailed;
        _shell.Dispose();

        if (_subtitleOverlay.IsVisible)
        {
            _subtitleOverlay.Close();
        }

        base.OnClosed(e);
    }

    private void HandleOpened(object? sender, EventArgs e)
    {
        if (!_subtitleOverlay.IsVisible)
        {
            _subtitleOverlay.Show(this);
        }

        _ = InitializeSubtitleWorkflowAsync();
        SyncSubtitleOverlay();
    }

    private void HandleVideoSurfaceLayoutUpdated(object? sender, EventArgs e)
    {
        SyncSubtitleOverlay();
    }

    private void HandleOverlayPositionInvalidated(object? sender, EventArgs e)
    {
        SyncSubtitleOverlay();
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

            if (!_startupMediaLoaded && File.Exists(GetBundledTestVideoPath()))
            {
                _startupMediaLoaded = true;
                await OpenMediaAsync(GetBundledTestVideoPath(), "Loading demo clip...");
            }
        }
        catch (Exception ex)
        {
            UpdateStatus($"Failed to initialize libmpv: {ex.Message}");
        }
    }

    private void HandleProjectionChanged(BabelPlayer.App.ShellProjectionSnapshot projection)
    {
        Dispatcher.UIThread.Post(() => ApplyProjection(projection));
    }

    private void HandleSubtitleSnapshotChanged(SubtitleWorkflowSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            ApplySubtitlePresentation(snapshot);
            await ApplyCaptionStartupGateAsync(snapshot);
        });
    }

    private void HandleSubtitleStatusChanged(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                UpdateStatus(message);
            }
        });
    }

    private void HandleMediaOpened(BabelPlayer.App.ShellPlaybackStateSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var result = await _shell.ShellPlaybackCommands.HandleMediaOpenedAsync(
                snapshot,
                _shell.ShellPreferencesService.Current,
                CancellationToken.None);
            UpdateStatus(result.ResumePosition is TimeSpan
                ? $"Resumed {Path.GetFileName(snapshot.Path)}."
                : result.StatusMessage);
        });
    }

    private void HandleMediaEnded(BabelPlayer.App.ShellPlaybackStateSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var result = _shell.ShellPlaybackCommands.HandleMediaEnded(_shell.ShellPreferencesService.Current.ResumeEnabled);
            if (result.NextItem is null)
            {
                UpdateStatus(result.StatusMessage);
                return;
            }

            await OpenQueueItemAsync(result.NextItem, result.StatusMessage);
        });
    }

    private void HandleMediaFailed(string message)
    {
        Dispatcher.UIThread.Post(() => UpdateStatus(message));
    }

    private void ApplyProjection(BabelPlayer.App.ShellProjectionSnapshot projection)
    {
        var transport = projection.Transport;

        if (_playPauseButton is not null)
        {
            _playPauseButton.Content = transport.IsPaused ? "Play" : "Pause";
            _playPauseButton.IsEnabled = !string.IsNullOrWhiteSpace(transport.Path);
        }

        if (_positionTextBlock is not null)
        {
            _positionTextBlock.Text = $"{FormatClock(transport.PositionSeconds)} / {FormatClock(transport.DurationSeconds)}";
        }

        if (_timelineSlider is not null)
        {
            _updatingTimelineFromProjection = true;
            _timelineSlider.Maximum = Math.Max(transport.DurationSeconds, 1);
            _timelineSlider.Value = Math.Clamp(transport.PositionSeconds, 0, _timelineSlider.Maximum);
            _timelineSlider.IsEnabled = transport.IsSeekable && !string.IsNullOrWhiteSpace(transport.Path);
            _updatingTimelineFromProjection = false;
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
            if (files.Count == 0)
            {
                return;
            }

            if (_videoHost?.HostHandle is > 0 && !_backendInitialized)
            {
                await InitializePlaybackAsync(_videoHost.HostHandle);
            }

            if (!_backendInitialized)
            {
                UpdateStatus("Playback surface is not ready yet.");
                return;
            }

            await OpenMediaFilesAsync(files, "Opening selected media...");
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

        _ = _shell.ShellPlaybackCommands.ApplyAudioPreferencesAsync(slider.Value, _shell.ShellProjectionReader.Current.Transport.IsMuted, CancellationToken.None);
    }

    private void HandleTimelineSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_updatingTimelineFromProjection || e.Property.Name != nameof(Slider.Value) || !_backendInitialized || sender is not Slider slider)
        {
            return;
        }

        var transport = _shell.ShellProjectionReader.Current.Transport;
        if (!transport.IsSeekable || string.IsNullOrWhiteSpace(transport.Path))
        {
            return;
        }

        _ = _shell.ShellPlaybackCommands.SeekAsync(TimeSpan.FromSeconds(slider.Value), CancellationToken.None);
    }

    private void HandleWindowDragOver(object? sender, DragEventArgs e)
    {
        var files = GetDroppedVideoPaths(e.Data);
        e.DragEffects = files.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void HandleWindowDrop(object? sender, DragEventArgs e)
    {
        try
        {
            var files = GetDroppedVideoPaths(e.Data);
            if (files.Count == 0)
            {
                UpdateStatus("Drop a supported video file to start playback.");
                return;
            }

            if (_videoHost?.HostHandle is > 0 && !_backendInitialized)
            {
                await InitializePlaybackAsync(_videoHost.HostHandle);
            }

            if (!_backendInitialized)
            {
                UpdateStatus("Playback surface is not ready yet.");
                return;
            }

            await OpenMediaFilesAsync(files, files.Count == 1
                ? $"Opening {Path.GetFileName(files[0])}..."
                : $"Opening {files.Count} queued videos...");
        }
        catch (Exception ex)
        {
            UpdateStatus($"Drop failed: {ex.Message}");
        }
        finally
        {
            e.Handled = true;
        }
    }

    private void UpdateStatus(string message)
    {
        if (_statusTextBlock is not null)
        {
            _statusTextBlock.Text = message;
        }
    }

    private void SyncSubtitleOverlay()
    {
        if (_videoSurfaceBorder is null)
        {
            return;
        }

        if (WindowState == WindowState.Minimized || !_subtitleOverlay.HasVisibleContent)
        {
            _subtitleOverlay.Hide();
            return;
        }

        var topLevel = TopLevel.GetTopLevel(_videoSurfaceBorder);
        if (topLevel is null || _videoSurfaceBorder.Bounds.Width <= 0 || _videoSurfaceBorder.Bounds.Height <= 0)
        {
            return;
        }

        var origin = _videoSurfaceBorder.PointToScreen(new Point(0, 0));
        var size = _videoSurfaceBorder.Bounds.Size;

        if (!_subtitleOverlay.IsVisible)
        {
            _subtitleOverlay.Show(this);
        }

        _subtitleOverlay.Position = origin;
        _subtitleOverlay.Width = size.Width;
        _subtitleOverlay.Height = size.Height;
        _subtitleOverlay.InvalidateMeasure();
    }

    private async Task InitializeSubtitleWorkflowAsync()
    {
        if (_subtitleWorkflowInitialized)
        {
            return;
        }

        try
        {
            await _shell.SubtitleWorkflowService.InitializeAsync(CancellationToken.None);
            _subtitleWorkflowInitialized = true;
            ApplySubtitlePresentation(_shell.SubtitleWorkflowService.Current);
        }
        catch (Exception ex)
        {
            UpdateStatus($"Subtitle workflow init failed: {ex.Message}");
        }
    }

    private async Task OpenMediaAsync(string path, string statusMessage)
    {
        await OpenMediaFilesAsync([path], statusMessage);
    }

    private async Task OpenMediaFilesAsync(IReadOnlyList<string> paths, string statusMessage)
    {
        var normalizedPaths = paths
            .Where(IsSupportedVideoPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length == 0)
        {
            UpdateStatus("Select a supported video file.");
            return;
        }

        UpdateStatus(statusMessage);
        await InitializeSubtitleWorkflowAsync();

        var queueResult = normalizedPaths.Length == 1
            ? _shell.QueueCommands.PlayNow(normalizedPaths[0])
            : _shell.QueueCommands.EnqueueFiles(normalizedPaths, autoplay: true);
        if (queueResult.ItemToLoad is null)
        {
            UpdateStatus(queueResult.StatusMessage ?? "Nothing to load.");
            return;
        }

        await OpenQueueItemAsync(queueResult.ItemToLoad, queueResult.StatusMessage ?? statusMessage);
    }

    private async Task OpenQueueItemAsync(ShellPlaylistItem item, string statusMessage)
    {
        var loaded = await _shell.ShellPlaybackCommands.LoadPlaybackItemAsync(
            item,
            BuildLoadOptions(),
            CancellationToken.None);
        UpdateStatus(loaded ? statusMessage : $"Unable to open {item.DisplayName}.");
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

    private void EnsureSubtitlesVisible()
    {
        var current = _shell.ShellPreferencesService.Current;
        if (current.SubtitleRenderMode != ShellSubtitleRenderMode.Off)
        {
            return;
        }

        _shell.ShellPreferencesService.ApplySubtitlePresentationChange(new ShellSubtitlePresentationChange(
            ShellSubtitleRenderMode.TranslationOnly,
            current.SubtitleStyle));
    }

    private void ApplySubtitlePresentation(SubtitleWorkflowSnapshot snapshot)
    {
        var preferences = _shell.ShellPreferencesService.Current;
        var presentation = _shell.SubtitleWorkflowService.GetOverlayPresentation(
            preferences.SubtitleRenderMode,
            subtitlesVisible: preferences.SubtitleRenderMode != ShellSubtitleRenderMode.Off);
        _subtitleOverlay.ApplyStyle(preferences.SubtitleStyle);
        _subtitleOverlay.SetPresentation(presentation);
        SyncSubtitleOverlay();
    }

    private async Task ApplyCaptionStartupGateAsync(SubtitleWorkflowSnapshot snapshot)
    {
        var preferences = _shell.ShellPreferencesService.Current;
        if (preferences.SubtitleRenderMode == ShellSubtitleRenderMode.Off || !_backendInitialized)
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

    private static string GetBundledTestVideoPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Assets", "test-video.mp4");
    }

    private static IReadOnlyList<string> GetDroppedVideoPaths(IDataObject data)
    {
        var files = data.GetFiles();
        if (files is null)
        {
            return [];
        }

        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path) && IsSupportedVideoPath(path))
            .Cast<string>()
            .ToArray();
    }

    private static bool IsSupportedVideoPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return AvaloniaFilePickerService.SupportedVideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatClock(double totalSeconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(totalSeconds, 0));
        var totalMinutes = (int)value.TotalMinutes;
        return $"{totalMinutes:00}:{value.Seconds:00}";
    }
}
