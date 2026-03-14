using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.Avalonia;

public partial class MainWindow : Window
{
    private readonly AvaloniaShellDependencies _shell;
    private readonly SubtitleOverlayWindow _subtitleOverlay = new();
    private Border? _videoSurfaceBorder;
    private MpvNativeHost? _videoHost;
    private ComboBox? _transcriptionModelComboBox;
    private ComboBox? _translationModelComboBox;
    private ComboBox? _subtitleModeComboBox;
    private ToggleSwitch? _autoTranslateToggleSwitch;
    private Button? _subtitleStyleButton;
    private Button? _fullscreenSubtitleModeButton;
    private Button? _fullscreenSubtitleStyleButton;
    private Button? _playPauseButton;
    private TextBlock? _positionTextBlock;
    private TextBlock? _statusTextBlock;
    private TextBlock? _fullscreenSubtitleSourceTextBlock;
    private Border? _resumePromptBorder;
    private TextBlock? _resumePromptTextBlock;
    private Slider? _timelineSlider;
    private Slider? _volumeSlider;
    private ComboBox? _playbackRateComboBox;
    private Slider? _sourceFontSizeSlider;
    private Slider? _translationFontSizeSlider;
    private Slider? _backgroundOpacitySlider;
    private Slider? _bottomMarginSlider;
    private TextBlock? _sourceFontSizeValueTextBlock;
    private TextBlock? _translationFontSizeValueTextBlock;
    private TextBlock? _backgroundOpacityValueTextBlock;
    private TextBlock? _bottomMarginValueTextBlock;
    private FlyoutBase? _subtitleStyleFlyout;
    private SubtitleWorkflowController? _subtitleRuntimeProgressSource;
    private RuntimeProgressWindow? _runtimeProgressWindow;
    private DispatcherTimer? _runtimeProgressCloseTimer;
    private Task? _runtimeProgressDialogTask;
    private ShellRuntimeInstallProgress? _latestRuntimeInstallProgress;
    private string _latestRuntimeLabel = "runtime";
    private bool _backendInitialized;
    private bool _subtitleWorkflowInitialized;
    private bool _startupMediaLoaded;
    private bool _closingRuntimeProgressWindow;
    private bool _runtimeProgressWindowDismissed;
    private bool _suppressWorkflowControlEvents;
    private bool _suppressStyleEvents;
    private bool _updatingTimelineFromProjection;
    private bool _updatingVolumeFromProjection;
    private bool _updatingPlaybackRateFromProjection;
    private string? _currentMediaPath;
    private PlaybackResumeEntry? _pendingResumeEntry;
    private string? _pendingResumePath;

    public MainWindow()
    {
        InitializeComponent();
        _shell = new AvaloniaShellCompositionRoot().Create(this);
        _runtimeProgressCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _runtimeProgressCloseTimer.Tick += HandleRuntimeProgressCloseTimerTick;

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
        _transcriptionModelComboBox ??= this.FindControl<ComboBox>("TranscriptionModelComboBox");
        _translationModelComboBox ??= this.FindControl<ComboBox>("TranslationModelComboBox");
        _subtitleModeComboBox ??= this.FindControl<ComboBox>("SubtitleModeComboBox");
        _autoTranslateToggleSwitch ??= this.FindControl<ToggleSwitch>("AutoTranslateToggleSwitch");
        _subtitleStyleButton ??= this.FindControl<Button>("SubtitleStyleButton");
        _fullscreenSubtitleModeButton ??= this.FindControl<Button>("FullscreenSubtitleModeButton");
        _fullscreenSubtitleStyleButton ??= this.FindControl<Button>("FullscreenSubtitleStyleButton");
        _playPauseButton ??= this.FindControl<Button>("PlayPauseButton");
        _positionTextBlock ??= this.FindControl<TextBlock>("PositionTextBlock");
        _statusTextBlock ??= this.FindControl<TextBlock>("StatusTextBlock");
        _fullscreenSubtitleSourceTextBlock ??= this.FindControl<TextBlock>("FullscreenSubtitleSourceTextBlock");
        _resumePromptBorder ??= this.FindControl<Border>("ResumePromptBorder");
        _resumePromptTextBlock ??= this.FindControl<TextBlock>("ResumePromptTextBlock");
        _timelineSlider ??= this.FindControl<Slider>("TimelineSlider");
        _volumeSlider ??= this.FindControl<Slider>("VolumeSlider");
        _playbackRateComboBox ??= this.FindControl<ComboBox>("PlaybackRateComboBox");
        _sourceFontSizeSlider ??= this.FindControl<Slider>("SourceFontSizeSlider");
        _translationFontSizeSlider ??= this.FindControl<Slider>("TranslationFontSizeSlider");
        _backgroundOpacitySlider ??= this.FindControl<Slider>("BackgroundOpacitySlider");
        _bottomMarginSlider ??= this.FindControl<Slider>("BottomMarginSlider");
        _sourceFontSizeValueTextBlock ??= this.FindControl<TextBlock>("SourceFontSizeValueTextBlock");
        _translationFontSizeValueTextBlock ??= this.FindControl<TextBlock>("TranslationFontSizeValueTextBlock");
        _backgroundOpacityValueTextBlock ??= this.FindControl<TextBlock>("BackgroundOpacityValueTextBlock");
        _bottomMarginValueTextBlock ??= this.FindControl<TextBlock>("BottomMarginValueTextBlock");
        _subtitleStyleFlyout ??= _subtitleStyleButton is null ? null : FlyoutBase.GetAttachedFlyout(_subtitleStyleButton);
        InitializePanelControls();
        InitializeShortcutAndWindowModeControls();

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

        if (_sourceFontSizeSlider is not null)
        {
            _sourceFontSizeSlider.PropertyChanged -= SubtitleStyleSlider_PropertyChanged;
            _sourceFontSizeSlider.PropertyChanged += SubtitleStyleSlider_PropertyChanged;
        }

        if (_translationFontSizeSlider is not null)
        {
            _translationFontSizeSlider.PropertyChanged -= SubtitleStyleSlider_PropertyChanged;
            _translationFontSizeSlider.PropertyChanged += SubtitleStyleSlider_PropertyChanged;
        }

        if (_backgroundOpacitySlider is not null)
        {
            _backgroundOpacitySlider.PropertyChanged -= SubtitleStyleSlider_PropertyChanged;
            _backgroundOpacitySlider.PropertyChanged += SubtitleStyleSlider_PropertyChanged;
        }

        if (_bottomMarginSlider is not null)
        {
            _bottomMarginSlider.PropertyChanged -= SubtitleStyleSlider_PropertyChanged;
            _bottomMarginSlider.PropertyChanged += SubtitleStyleSlider_PropertyChanged;
        }


        _shell.ShellProjectionReader.ProjectionChanged -= HandleProjectionChanged;
        _shell.ShellProjectionReader.ProjectionChanged += HandleProjectionChanged;
        _shell.SubtitleWorkflowService.SnapshotChanged -= HandleSubtitleSnapshotChanged;
        _shell.SubtitleWorkflowService.SnapshotChanged += HandleSubtitleSnapshotChanged;
        _shell.SubtitleWorkflowService.StatusChanged -= HandleSubtitleStatusChanged;
        _shell.SubtitleWorkflowService.StatusChanged += HandleSubtitleStatusChanged;
        _shell.CredentialSetupService.SnapshotChanged -= HandleCredentialSetupSnapshotChanged;
        _shell.CredentialSetupService.SnapshotChanged += HandleCredentialSetupSnapshotChanged;
        _shell.PlaybackHostRuntime.MediaOpened -= HandleMediaOpened;
        _shell.PlaybackHostRuntime.MediaOpened += HandleMediaOpened;
        _shell.PlaybackHostRuntime.MediaEnded -= HandleMediaEnded;
        _shell.PlaybackHostRuntime.MediaEnded += HandleMediaEnded;
        _shell.PlaybackHostRuntime.MediaFailed -= HandleMediaFailed;
        _shell.PlaybackHostRuntime.MediaFailed += HandleMediaFailed;
        _shell.PlaybackHostRuntime.RuntimeInstallProgress -= HandlePlaybackRuntimeInstallProgress;
        _shell.PlaybackHostRuntime.RuntimeInstallProgress += HandlePlaybackRuntimeInstallProgress;
        if (_shell.SubtitleWorkflowService is SubtitleWorkflowController subtitleRuntimeProgressSource)
        {
            if (_subtitleRuntimeProgressSource is not null)
            {
                _subtitleRuntimeProgressSource.RuntimeInstallProgressChanged -= HandleSubtitleRuntimeInstallProgress;
            }

            _subtitleRuntimeProgressSource = subtitleRuntimeProgressSource;
            _subtitleRuntimeProgressSource.RuntimeInstallProgressChanged -= HandleSubtitleRuntimeInstallProgress;
            _subtitleRuntimeProgressSource.RuntimeInstallProgressChanged += HandleSubtitleRuntimeInstallProgress;
        }

        EnsureSubtitlesVisible();
        ApplyProjection(_shell.ShellProjectionReader.Current);
        ApplyWorkflowSnapshot(_shell.SubtitleWorkflowService.Current);
        ApplySubtitlePresentation(_shell.SubtitleWorkflowService.Current);
        UpdateStatus(File.Exists(GetBundledTestVideoPath())
            ? $"Loading demo clip: {Path.GetFileName(GetBundledTestVideoPath())}"
            : "Open a local video to begin playback.");
        SyncSubtitleOverlay();
        TryShowRuntimeProgressWindow();
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

        if (_sourceFontSizeSlider is not null)
        {
            _sourceFontSizeSlider.PropertyChanged -= SubtitleStyleSlider_PropertyChanged;
        }

        if (_translationFontSizeSlider is not null)
        {
            _translationFontSizeSlider.PropertyChanged -= SubtitleStyleSlider_PropertyChanged;
        }

        if (_backgroundOpacitySlider is not null)
        {
            _backgroundOpacitySlider.PropertyChanged -= SubtitleStyleSlider_PropertyChanged;
        }

        if (_bottomMarginSlider is not null)
        {
            _bottomMarginSlider.PropertyChanged -= SubtitleStyleSlider_PropertyChanged;
        }

        _shell.ShellProjectionReader.ProjectionChanged -= HandleProjectionChanged;
        _shell.SubtitleWorkflowService.SnapshotChanged -= HandleSubtitleSnapshotChanged;
        _shell.SubtitleWorkflowService.StatusChanged -= HandleSubtitleStatusChanged;
        _shell.CredentialSetupService.SnapshotChanged -= HandleCredentialSetupSnapshotChanged;
        _shell.PlaybackHostRuntime.MediaOpened -= HandleMediaOpened;
        _shell.PlaybackHostRuntime.MediaEnded -= HandleMediaEnded;
        _shell.PlaybackHostRuntime.MediaFailed -= HandleMediaFailed;
        _shell.PlaybackHostRuntime.RuntimeInstallProgress -= HandlePlaybackRuntimeInstallProgress;
        if (_subtitleRuntimeProgressSource is not null)
        {
            _subtitleRuntimeProgressSource.RuntimeInstallProgressChanged -= HandleSubtitleRuntimeInstallProgress;
            _subtitleRuntimeProgressSource = null;
        }
        if (_runtimeProgressCloseTimer is not null)
        {
            _runtimeProgressCloseTimer.Stop();
            _runtimeProgressCloseTimer.Tick -= HandleRuntimeProgressCloseTimerTick;
        }
        CloseRuntimeProgressWindow();
        DisposePanelControls();
        DisposeShortcutAndWindowModeControls();
        _shell.ShellPlaybackCommands.FlushResumeTracking();
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

        TryShowRuntimeProgressWindow();
        _ = InitializeSubtitleWorkflowAsync();
        _ = EnsureWindowModeInitializedAsync();
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
            CloseRuntimeProgressWindow();
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

    private void HandleCredentialSetupSnapshotChanged(CredentialSetupSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => ApplyWorkflowSnapshot(_shell.SubtitleWorkflowService.Current));
    }

    private void HandleMediaOpened(BabelPlayer.App.ShellPlaybackStateSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var path = snapshot.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                HideResumePrompt();
                UpdateStatus("Media opened.");
                return;
            }

            if (!_shell.ShellPreferencesService.Current.ResumeEnabled)
            {
                HideResumePrompt();
                UpdateStatus($"Now playing {Path.GetFileName(path)}.");
                return;
            }

            var entry = _shell.ResumePlaybackService.FindEntry(path, snapshot.Duration);
            if (entry is null)
            {
                HideResumePrompt();
                UpdateStatus($"Now playing {Path.GetFileName(path)}.");
                return;
            }

            _pendingResumeEntry = entry;
            _pendingResumePath = path;
            await _shell.ShellPlaybackCommands.PauseAsync(CancellationToken.None);
            ShowResumePrompt(entry);
        });
    }

    private void HandleMediaEnded(BabelPlayer.App.ShellPlaybackStateSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            Title = "Babel Player";
            HideResumePrompt();
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
        Dispatcher.UIThread.Post(() =>
        {
            Title = "Babel Player";
            HideResumePrompt();
            CloseRuntimeProgressWindow();
            UpdateStatus(message);
        });
    }

    private void HandlePlaybackRuntimeInstallProgress(ShellRuntimeInstallProgress progress)
    {
        Dispatcher.UIThread.Post(() => ApplyRuntimeInstallProgress("mpv runtime", progress));
    }

    private void HandleSubtitleRuntimeInstallProgress(ShellRuntimeInstallProgress progress)
    {
        Dispatcher.UIThread.Post(() => ApplyRuntimeInstallProgress("subtitle runtime", progress));
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

        if (_playbackRateComboBox is not null)
        {
            _updatingPlaybackRateFromProjection = true;
            var expected = $"{transport.PlaybackRate:0.##}x";
            _playbackRateComboBox.SelectedItem = _playbackRateComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Content?.ToString(), expected, StringComparison.Ordinal))
                ?? _playbackRateComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Content?.ToString(), "1.0x", StringComparison.Ordinal));
            _updatingPlaybackRateFromProjection = false;
        }

        Title = string.IsNullOrWhiteSpace(transport.Path)
            ? "Babel Player"
            : $"{Path.GetFileName(transport.Path)} — Babel Player";

        if (!string.IsNullOrWhiteSpace(transport.Path)
            && !string.Equals(_currentMediaPath, transport.Path, StringComparison.OrdinalIgnoreCase))
        {
            _currentMediaPath = transport.Path;
            UpdateStatus(Path.GetFileName(transport.Path));
        }
    }

    private void ApplyWorkflowSnapshot(SubtitleWorkflowSnapshot snapshot)
    {
        _suppressWorkflowControlEvents = true;
        try
        {
            if (_transcriptionModelComboBox is not null)
            {
                var options = BuildTranscriptionModelOptions(snapshot).ToArray();
                _transcriptionModelComboBox.ItemsSource = options;
                _transcriptionModelComboBox.SelectedItem = options
                    .FirstOrDefault(option => string.Equals(option.Key, snapshot.SelectedTranscriptionModelKey, StringComparison.Ordinal));
            }

            if (_translationModelComboBox is not null)
            {
                var options = BuildTranslationModelOptions(snapshot).ToArray();
                _translationModelComboBox.ItemsSource = options;
                _translationModelComboBox.SelectedItem = options
                    .FirstOrDefault(option => string.Equals(option.Key, snapshot.SelectedTranslationModelKey, StringComparison.Ordinal));
            }

            if (_autoTranslateToggleSwitch is not null)
            {
                _autoTranslateToggleSwitch.IsChecked = snapshot.AutoTranslateEnabled;
            }

            if (_subtitleModeComboBox is not null)
            {
                var effectiveMode = GetEffectiveSubtitleRenderMode();
                foreach (var item in _subtitleModeComboBox.Items.OfType<ComboBoxItem>())
                {
                    if (TryParseSubtitleMode(item.Tag as string, out var mode) && mode == effectiveMode)
                    {
                        _subtitleModeComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            if (_fullscreenSubtitleModeButton is not null)
            {
                _fullscreenSubtitleModeButton.Content = $"Subtitles: {FormatSubtitleModeButtonText(GetEffectiveSubtitleRenderMode())}";
            }

            if (_fullscreenSubtitleSourceTextBlock is not null)
            {
                _fullscreenSubtitleSourceTextBlock.Text = string.IsNullOrWhiteSpace(snapshot.CaptionGenerationModeLabel)
                    ? "Subtitle source unavailable."
                    : snapshot.CaptionGenerationModeLabel;
            }
        }
        finally
        {
            _suppressWorkflowControlEvents = false;
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

    private async void ImportSubtitlesButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!StorageProvider.CanOpen)
            {
                UpdateStatus("Subtitle import is not available on this platform.");
                return;
            }

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Subtitles",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Subtitle files")
                    {
                        Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt", "*.sub"]
                    }
                ]
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var result = await _shell.SubtitleWorkflowService.ImportExternalSubtitlesAsync(path, autoLoaded: false, CancellationToken.None);
            if (result.CueCount > 0)
            {
                UpdateStatus($"Loaded subtitles from {Path.GetFileName(path)}.");
                ApplySubtitlePresentation(_shell.SubtitleWorkflowService.Current);
                return;
            }

            UpdateStatus("No subtitles found in the selected file.");
        }
        catch (Exception ex)
        {
            UpdateStatus($"Subtitle import failed: {ex.Message}");
        }
    }

    private async void TranscriptionModelComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorkflowControlEvents || sender is not ComboBox comboBox || comboBox.SelectedItem is not TranscriptionModelOption option)
        {
            return;
        }

        try
        {
            await PrepareForTranscriptionRefreshAsync();
            var applied = await _shell.SubtitleWorkflowService.SelectTranscriptionModelAsync(option.Key, CancellationToken.None);
            if (!applied)
            {
                ApplyWorkflowSnapshot(_shell.SubtitleWorkflowService.Current);
                return;
            }

            ApplyWorkflowSnapshot(_shell.SubtitleWorkflowService.Current);
        }
        catch (Exception ex)
        {
            UpdateStatus($"Transcription model change failed: {ex.Message}");
            ApplyWorkflowSnapshot(_shell.SubtitleWorkflowService.Current);
        }
    }

    private async void TranslationModelComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorkflowControlEvents || sender is not ComboBox comboBox || comboBox.SelectedItem is not TranslationModelOption option)
        {
            return;
        }

        try
        {
            var applied = await _shell.SubtitleWorkflowService.SelectTranslationModelAsync(option.Key, CancellationToken.None);
            ApplyWorkflowSnapshot(_shell.SubtitleWorkflowService.Current);
            if (!applied)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            UpdateStatus($"Translation model change failed: {ex.Message}");
            ApplyWorkflowSnapshot(_shell.SubtitleWorkflowService.Current);
        }
    }

    private async void AutoTranslateToggleSwitch_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressWorkflowControlEvents || _autoTranslateToggleSwitch is null)
        {
            return;
        }

        try
        {
            await _shell.SubtitleWorkflowService.SetAutoTranslateEnabledAsync(_autoTranslateToggleSwitch.IsChecked == true, CancellationToken.None);
            ApplyWorkflowSnapshot(_shell.SubtitleWorkflowService.Current);
        }
        catch (Exception ex)
        {
            UpdateStatus($"Auto-translate update failed: {ex.Message}");
            ApplyWorkflowSnapshot(_shell.SubtitleWorkflowService.Current);
        }
    }

    private void SubtitleModeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorkflowControlEvents || sender is not ComboBox comboBox || comboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (!TryParseSubtitleMode(item.Tag as string, out var selectedMode))
        {
            return;
        }

        ApplySubtitleRenderMode(selectedMode);
    }

    private void FullscreenSubtitleModeButton_Click(object? sender, RoutedEventArgs e)
    {
        CycleSubtitleRenderMode();
    }

    private void FullscreenSubtitleStyleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_subtitleStyleFlyout is not null && _fullscreenSubtitleStyleButton is not null)
        {
            _subtitleStyleFlyout.ShowAt(_fullscreenSubtitleStyleButton);
            return;
        }

        UpdateStatus("Subtitle style not available yet.");
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

    private async void PlaybackRateComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingPlaybackRateFromProjection
            || !_backendInitialized
            || sender is not ComboBox comboBox
            || comboBox.SelectedItem is not ComboBoxItem item
            || !TryParsePlaybackRate(item.Content?.ToString(), out var playbackRate))
        {
            return;
        }

        var current = _shell.ShellPreferencesService.Current;
        await _shell.PlaybackHostRuntime.SetPlaybackRateAsync(playbackRate, CancellationToken.None);
        await _shell.ShellPreferenceCommands.ApplyPlaybackDefaultsAsync(new ShellPlaybackDefaultsChange(
            current.HardwareDecodingMode,
            playbackRate,
            current.AudioDelaySeconds,
            current.SubtitleDelaySeconds,
            current.AspectRatio),
            CancellationToken.None);
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

    private void ApplyRuntimeInstallProgress(string runtimeLabel, ShellRuntimeInstallProgress progress)
    {
        _latestRuntimeLabel = runtimeLabel;
        _latestRuntimeInstallProgress = progress;
        UpdateStatus($"Downloading {progress.Stage}... {progress.BytesTransferred / 1_048_576.0:F1} MB");
        TryShowRuntimeProgressWindow();
        _runtimeProgressWindow?.ApplyProgress(runtimeLabel, progress);

        if (string.Equals(progress.Stage, "ready", StringComparison.OrdinalIgnoreCase)
            || progress.ProgressRatio is >= 1.0)
        {
            ScheduleRuntimeProgressWindowClose();
            return;
        }

        _runtimeProgressCloseTimer?.Stop();
    }

    private void TryShowRuntimeProgressWindow()
    {
        if (_runtimeProgressWindowDismissed
            || _runtimeProgressWindow is not null
            || _latestRuntimeInstallProgress is null
            || !IsVisible)
        {
            return;
        }

        _runtimeProgressWindow = new RuntimeProgressWindow();
        _runtimeProgressWindow.Closed += HandleRuntimeProgressWindowClosed;
        _runtimeProgressWindow.Opened += HandleRuntimeProgressWindowOpened;
        _runtimeProgressWindow.ApplyProgress(_latestRuntimeLabel, _latestRuntimeInstallProgress);
        _runtimeProgressDialogTask = _runtimeProgressWindow.ShowDialog(this);
    }

    private void HandleRuntimeProgressWindowOpened(object? sender, EventArgs e)
    {
        if (_runtimeProgressWindow is not null && _latestRuntimeInstallProgress is not null)
        {
            _runtimeProgressWindow.ApplyProgress(_latestRuntimeLabel, _latestRuntimeInstallProgress);
        }
    }

    private void HandleRuntimeProgressWindowClosed(object? sender, EventArgs e)
    {
        if (!_closingRuntimeProgressWindow)
        {
            _runtimeProgressWindowDismissed = true;
        }

        if (_runtimeProgressWindow is not null)
        {
            _runtimeProgressWindow.Closed -= HandleRuntimeProgressWindowClosed;
            _runtimeProgressWindow.Opened -= HandleRuntimeProgressWindowOpened;
        }

        _runtimeProgressWindow = null;
        _runtimeProgressDialogTask = null;
        _closingRuntimeProgressWindow = false;
    }

    private void ScheduleRuntimeProgressWindowClose()
    {
        if (_runtimeProgressCloseTimer is null)
        {
            return;
        }

        _runtimeProgressCloseTimer.Stop();
        _runtimeProgressCloseTimer.Start();
    }

    private void HandleRuntimeProgressCloseTimerTick(object? sender, EventArgs e)
    {
        _runtimeProgressCloseTimer?.Stop();
        CloseRuntimeProgressWindow();
    }

    private void CloseRuntimeProgressWindow()
    {
        _runtimeProgressCloseTimer?.Stop();

        if (_runtimeProgressWindow is null)
        {
            return;
        }

        _closingRuntimeProgressWindow = true;
        _runtimeProgressWindow.Close();
    }

    private void UpdateStatus(string message)
    {
        if (_statusTextBlock is not null)
        {
            _statusTextBlock.Text = message;
        }
    }

    private async void ResumeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_pendingResumeEntry is null || string.IsNullOrWhiteSpace(_pendingResumePath))
        {
            HideResumePrompt();
            return;
        }

        var path = _pendingResumePath;
        var resumePosition = TimeSpan.FromSeconds(Math.Max(_pendingResumeEntry.PositionSeconds, 0));
        HideResumePrompt();
        await _shell.ShellPlaybackCommands.SeekAsync(resumePosition, CancellationToken.None);
        await _shell.ShellPlaybackCommands.PlayAsync(CancellationToken.None);
        UpdateStatus($"Resumed {Path.GetFileName(path)} from {FormatClock(resumePosition.TotalSeconds)}.");
    }

    private async void StartOverButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingResumePath))
        {
            HideResumePrompt();
            return;
        }

        var path = _pendingResumePath;
        HideResumePrompt();
        _shell.ResumePlaybackService.RemoveCompletedEntry(path);
        await _shell.ShellPlaybackCommands.SeekAsync(TimeSpan.Zero, CancellationToken.None);
        await _shell.ShellPlaybackCommands.PlayAsync(CancellationToken.None);
        UpdateStatus($"Starting {Path.GetFileName(path)} from the beginning.");
    }

    private void ShowResumePrompt(PlaybackResumeEntry entry)
    {
        if (_resumePromptTextBlock is not null)
        {
            _resumePromptTextBlock.Text = $"Resume from {FormatClock(entry.PositionSeconds)}?";
        }

        if (_resumePromptBorder is not null)
        {
            _resumePromptBorder.IsVisible = true;
        }

        UpdateStatus($"Resume available at {FormatClock(entry.PositionSeconds)}.");
    }

    private void HideResumePrompt()
    {
        _pendingResumeEntry = null;
        _pendingResumePath = null;

        if (_resumePromptBorder is not null)
        {
            _resumePromptBorder.IsVisible = false;
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
            ApplyWorkflowSnapshot(_shell.SubtitleWorkflowService.Current);
            ApplySubtitlePresentation(_shell.SubtitleWorkflowService.Current);
        }
        catch (Exception ex)
        {
            CloseRuntimeProgressWindow();
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
        await ApplyQueueMutationAsync(queueResult);
    }

    private async Task OpenQueueItemAsync(ShellPlaylistItem item, string statusMessage)
    {
        HideResumePrompt();
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
        ApplyWorkflowSnapshot(snapshot);
        var preferences = _shell.ShellPreferencesService.Current;
        var presentation = _shell.SubtitleWorkflowService.GetOverlayPresentation(
            preferences.SubtitleRenderMode,
            subtitlesVisible: preferences.SubtitleRenderMode != ShellSubtitleRenderMode.Off);
        ApplySubtitleStyleControls(preferences.SubtitleStyle);
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

    private async Task PrepareForTranscriptionRefreshAsync()
    {
        var result = await _shell.ShellPlaybackCommands.PrepareForTranscriptionRefreshAsync(
            _shell.SubtitleWorkflowService.Current,
            _shell.ShellPlaybackCommands.CurrentPlaybackSnapshot,
            CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            UpdateStatus(result.StatusMessage);
        }
    }

    private void ApplySubtitleRenderMode(ShellSubtitleRenderMode selectedMode)
    {
        var currentPreferences = _shell.ShellPreferencesService.Current;
        var result = _shell.SubtitleWorkflowService.SelectRenderMode(selectedMode, currentPreferences.SubtitleRenderMode);
        _shell.ShellPreferencesService.ApplySubtitlePresentationChange(new ShellSubtitlePresentationChange(
            result.RequestedRenderMode,
            currentPreferences.SubtitleStyle));
        ApplyWorkflowSnapshot(_shell.SubtitleWorkflowService.Current);
        ApplySubtitlePresentation(_shell.SubtitleWorkflowService.Current);
        UpdateStatus($"Subtitle mode: {FormatSubtitleRenderModeLabel(result.EffectiveRenderMode)}.");
    }

    private static string GetBundledTestVideoPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Assets", "test-video.mp4");
    }

    private ShellSubtitleRenderMode GetEffectiveSubtitleRenderMode()
    {
        return _shell.SubtitleWorkflowService.GetEffectiveRenderMode(_shell.ShellPreferencesService.Current.SubtitleRenderMode);
    }

    private IEnumerable<TranscriptionModelOption> BuildTranscriptionModelOptions(SubtitleWorkflowSnapshot snapshot)
    {
        var models = snapshot.AvailableTranscriptionModels.Count > 0
            ? snapshot.AvailableTranscriptionModels
            : SubtitleWorkflowCatalog.AvailableTranscriptionModels;

        return models.Select(selection => new TranscriptionModelOption
        {
            Selection = selection,
            Availability = _shell.CredentialSetupService.GetTranscriptionAvailability(selection)
        });
    }

    private IEnumerable<TranslationModelOption> BuildTranslationModelOptions(SubtitleWorkflowSnapshot snapshot)
    {
        var models = snapshot.AvailableTranslationModels.Count > 0
            ? snapshot.AvailableTranslationModels
            : SubtitleWorkflowCatalog.AvailableTranslationModels;

        return models.Select(selection => new TranslationModelOption
        {
            Selection = selection,
            Availability = _shell.CredentialSetupService.GetTranslationAvailability(selection)
        });
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

    private static bool TryParsePlaybackRate(string? text, out double playbackRate)
    {
        playbackRate = 1.0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return double.TryParse(
            text.Replace("x", string.Empty, StringComparison.Ordinal),
            out playbackRate);
    }

    private static bool TryParseSubtitleMode(string? value, out ShellSubtitleRenderMode mode)
    {
        return Enum.TryParse(value, ignoreCase: false, out mode);
    }

    private static string FormatSubtitleRenderModeLabel(ShellSubtitleRenderMode mode)
    {
        return mode switch
        {
            ShellSubtitleRenderMode.Off => "off",
            ShellSubtitleRenderMode.SourceOnly => "source only",
            ShellSubtitleRenderMode.TranslationOnly => "translation only",
            ShellSubtitleRenderMode.Dual => "dual",
            _ => "translation only"
        };
    }

    private static string FormatSubtitleModeButtonText(ShellSubtitleRenderMode mode)
    {
        return mode switch
        {
            ShellSubtitleRenderMode.Off => "Off",
            ShellSubtitleRenderMode.SourceOnly => "Source",
            ShellSubtitleRenderMode.TranslationOnly => "Translation",
            ShellSubtitleRenderMode.Dual => "Dual",
            _ => "Translation"
        };
    }

    private void SubtitleStyleFlyout_Opened(object? sender, EventArgs e)
    {
        ApplySubtitleStyleControls(_shell.ShellPreferencesService.Current.SubtitleStyle);
    }

    private void SubtitleStyleSlider_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_suppressStyleEvents || e.Property.Name != nameof(Slider.Value))
        {
            return;
        }

        var updatedStyle = BuildSubtitleStyleFromControls();
        UpdateSubtitleStyleValueLabels(updatedStyle);

        var current = _shell.ShellPreferencesService.Current;
        _shell.ShellPreferencesService.ApplySubtitlePresentationChange(new ShellSubtitlePresentationChange(
            current.SubtitleRenderMode,
            updatedStyle));
        ApplySubtitlePresentation(_shell.SubtitleWorkflowService.Current);
    }

    private void ApplySubtitleStyleControls(ShellSubtitleStyle style)
    {
        _suppressStyleEvents = true;
        try
        {
            if (_sourceFontSizeSlider is not null)
            {
                _sourceFontSizeSlider.Value = style.SourceFontSize;
            }

            if (_translationFontSizeSlider is not null)
            {
                _translationFontSizeSlider.Value = style.TranslationFontSize;
            }

            if (_backgroundOpacitySlider is not null)
            {
                _backgroundOpacitySlider.Value = style.BackgroundOpacity;
            }

            if (_bottomMarginSlider is not null)
            {
                _bottomMarginSlider.Value = style.BottomMargin;
            }

            UpdateSubtitleStyleValueLabels(style);
        }
        finally
        {
            _suppressStyleEvents = false;
        }
    }

    private ShellSubtitleStyle BuildSubtitleStyleFromControls()
    {
        var currentStyle = _shell.ShellPreferencesService.Current.SubtitleStyle;
        return currentStyle with
        {
            SourceFontSize = Math.Clamp(Math.Round(_sourceFontSizeSlider?.Value ?? currentStyle.SourceFontSize), 14, 48),
            TranslationFontSize = Math.Clamp(Math.Round(_translationFontSizeSlider?.Value ?? currentStyle.TranslationFontSize), 14, 48),
            BackgroundOpacity = Math.Clamp(Math.Round((_backgroundOpacitySlider?.Value ?? currentStyle.BackgroundOpacity) / 0.05) * 0.05, 0, 1),
            BottomMargin = Math.Clamp(Math.Round((_bottomMarginSlider?.Value ?? currentStyle.BottomMargin) / 2) * 2, 0, 60)
        };
    }

    private void UpdateSubtitleStyleValueLabels(ShellSubtitleStyle style)
    {
        if (_sourceFontSizeValueTextBlock is not null)
        {
            _sourceFontSizeValueTextBlock.Text = $"{style.SourceFontSize:0}";
        }

        if (_translationFontSizeValueTextBlock is not null)
        {
            _translationFontSizeValueTextBlock.Text = $"{style.TranslationFontSize:0}";
        }

        if (_backgroundOpacityValueTextBlock is not null)
        {
            _backgroundOpacityValueTextBlock.Text = $"{style.BackgroundOpacity:P0}";
        }

        if (_bottomMarginValueTextBlock is not null)
        {
            _bottomMarginValueTextBlock.Text = $"{style.BottomMargin:0}";
        }
    }
}
