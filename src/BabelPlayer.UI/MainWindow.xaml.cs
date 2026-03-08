using Microsoft.Win32;
using BabelPlayer.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BabelPlayer.UI;

public partial class MainWindow : Window
{
    private const string DefaultLocalSpeechCulture = "en-US";
    private const string DefaultSubtitleModelKey = "local:base";
    private const double BrowserPanelWidth = 260;
    private const double PlaylistPanelWidth = 280;
    private readonly SubtitleManager _subtitleManager = new();
    private readonly MtService _translator = new();
    private readonly DispatcherTimer _subtitleTimer = new();
    private readonly DispatcherTimer _resumeTimer = new();
    private readonly object _translationSync = new();
    private readonly HashSet<SubtitleCue> _inFlightCueTranslations = [];
    private readonly ObservableCollection<PlaylistItem> _playlist = [];
    private readonly List<PlaybackResumeEntry> _resumeEntries = [];

    private CancellationTokenSource? _translationCts;
    private CancellationTokenSource? _captionGenerationCts;
    private string? _sessionOpenAiApiKey;
    private string? _currentVideoPath;
    private string? _currentFolderPath;
    private bool _isPaused;
    private bool _isSeeking;
    private bool _resumePlaybackAfterSeek;
    private bool _isPlaybackSurfaceHovered;
    private bool _isCaptionGenerationInProgress;
    private bool _autoResumePlaybackAfterCaptionReady;
    private bool _isTranslationEnabledForCurrentVideo;
    private bool _autoTranslateVideosOutsidePreferredLanguage;
    private bool _currentVideoTranslationPreferenceLocked;
    private bool _suppressTranslationMenuEvents;
    private string? _overlayStatusText;
    private string _captionGenerationModeLabel = "local";
    private string _selectedSubtitleModelKey = DefaultSubtitleModelKey;
    private string? _selectedTranslationModelKey;
    private string _selectedAspectRatio = "auto";
    private string _translationTargetLanguage = "en";
    private string _autoTranslatePreferredSourceLanguage = "en";
    private int _activeCaptionGenerationId;
    private string _currentSourceLanguage = "und";
    private int _playlistIndex = -1;
    private double _audioDelaySeconds;
    private double _subtitleDelaySeconds;
    private bool _isMuted;
    private AppPlayerSettings _appSettings = new();
    private List<MediaTrackInfo> _currentTracks = [];
    private SubtitlePipelineSource _subtitleSource = SubtitlePipelineSource.None;

    public MainWindow()
    {
        InitializeComponent();

        _appSettings = AppStateStore.LoadSettings();
        _resumeEntries = AppStateStore.LoadResumeEntries().ToList();

        _selectedSubtitleModelKey = GetPersistedSubtitleModelKey();
        _selectedTranslationModelKey = GetPersistedTranslationModelKey();
        _autoTranslateVideosOutsidePreferredLanguage = SecureSettingsStore.GetAutoTranslateEnabled();
        UpdateSubtitleModelMenuChecks();
        UpdateTranslationModelMenuChecks();
        UpdateAutoTranslateMenuChecks();
        UpdateSubtitleRenderModeMenuChecks();
        UpdateHardwareDecodingMenuChecks();
        UpdateAspectRatioMenuChecks();
        UpdatePanelVisibilityMenuChecks();
        RebuildAudioTrackMenu();
        RebuildEmbeddedSubtitleTrackMenu();

        HardwareStatusText.Text = HardwareDetector.GetSummary();
        PlaybackStatusText.Text = "Ready";
        _sessionOpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? SecureSettingsStore.GetOpenAiApiKey();
        _translator.OnLocalRuntimeStatus += HandleLocalTranslationRuntimeStatus;
        ConfigureTranslator();
        Player.HardwareDecodingMode = _appSettings.HardwareDecodingMode;
        Player.Volume = 0.8;
        PlaylistListBox.ItemsSource = _playlist;
        PlaybackSpeedComboBox.SelectedIndex = 3;
        LoadPlaybackSettings();
        RebuildLibraryTree();

        _subtitleTimer.Interval = TimeSpan.FromMilliseconds(120);
        _subtitleTimer.Tick += SubtitleTimer_Tick;
        _resumeTimer.Interval = TimeSpan.FromSeconds(5);
        _resumeTimer.Tick += ResumeTimer_Tick;

        Player.MediaOpened += Player_MediaOpened;
        Player.MediaEnded += Player_MediaEnded;
        Player.MediaFailed += Player_MediaFailed;
        Player.TracksChanged += Player_TracksChanged;
        Player.RuntimeInstallProgress += Player_RuntimeInstallProgress;

        Closed += MainWindow_Closed;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        ApplyWindowMode(_appSettings.WindowMode, persist: false);
        ApplySubtitleStyleSettings();
        UpdateTransportVisibility();
    }

    private async void OpenVideoButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = GetVideoFileDialogFilter(),
            Title = "Choose local videos",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        EnqueuePaths(dialog.FileNames, playFirstNewItem: true);
        await Task.CompletedTask;
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Player.Source is null)
        {
            return;
        }

        if (_isPaused)
        {
            Player.Play();
            _isPaused = false;
            PlayPauseButton.Content = "⏸";
        }
        else
        {
            Player.Pause();
            _isPaused = true;
            PlayPauseButton.Content = "▶";
        }

        UpdateTransportVisibility();
    }

    private void RewindButton_Click(object sender, RoutedEventArgs e)
    {
        SeekBy(TimeSpan.FromSeconds(-10));
    }

    private void FastForwardButton_Click(object sender, RoutedEventArgs e)
    {
        SeekBy(TimeSpan.FromSeconds(10));
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowStyle == WindowStyle.None && WindowState == WindowState.Maximized)
        {
            ApplyWindowMode(PlaybackWindowMode.Standard);
            return;
        }

        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        ResizeMode = ResizeMode.NoResize;
        FullscreenButton.Content = "🗗";
        PersistPlaybackSettings();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Player is null)
        {
            return;
        }

        Player.Volume = Math.Clamp(VolumeSlider.Value, 0, 1);
        _isMuted = Player.Volume <= 0.001;
        MuteButton.Content = _isMuted ? "🔇" : "🔊";
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        Player.SetMute(_isMuted);
        MuteButton.Content = _isMuted ? "🔇" : "🔊";
    }

    private void PreviousFrameButton_Click(object sender, RoutedEventArgs e)
    {
        Player.StepFrame(false);
        _isPaused = true;
        PlayPauseButton.Content = "▶";
    }

    private void NextFrameButton_Click(object sender, RoutedEventArgs e)
    {
        Player.StepFrame(true);
        _isPaused = true;
        PlayPauseButton.Content = "▶";
    }

    private void PlaybackSpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || PlaybackSpeedComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string speedText || !double.TryParse(speedText, out var speed))
        {
            return;
        }

        Player.SetPlaybackRate(speed);
        _appSettings = _appSettings with { DefaultPlaybackRate = speed };
        AppStateStore.SaveSettings(_appSettings);
    }

    private void PreviousPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playlistIndex > 0)
        {
            _ = PlayPlaylistIndexAsync(_playlistIndex - 1);
        }
    }

    private void NextPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playlistIndex + 1 < _playlist.Count)
        {
            _ = PlayPlaylistIndexAsync(_playlistIndex + 1);
        }
    }

    private void PiPButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetCurrentWindowMode() == PlaybackWindowMode.PictureInPicture)
        {
            ApplyWindowMode(PlaybackWindowMode.Standard);
            return;
        }

        ApplyWindowMode(PlaybackWindowMode.PictureInPicture);
    }

    private void AddFilesToPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = GetVideoFileDialogFilter(),
            Title = "Add local videos to playlist",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            EnqueuePaths(dialog.FileNames, playFirstNewItem: _playlist.Count == 0);
        }
    }

    private void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder to add to the playlist and browser"
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            EnqueuePaths([dialog.FolderName], playFirstNewItem: _playlist.Count == 0);
        }
    }

    private void ClearPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        _playlist.Clear();
        _playlistIndex = -1;
    }

    private void RemovePlaylistItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistListBox.SelectedItem is not PlaylistItem item)
        {
            return;
        }

        var removedIndex = _playlist.IndexOf(item);
        _playlist.Remove(item);
        if (_playlistIndex >= removedIndex)
        {
            _playlistIndex = Math.Max(-1, _playlistIndex - 1);
        }
    }

    private void PlaylistListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistListBox.SelectedIndex >= 0)
        {
            _playlistIndex = PlaylistListBox.SelectedIndex;
        }
    }

    private void PlaylistListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistListBox.SelectedIndex >= 0)
        {
            _ = PlayPlaylistIndexAsync(PlaylistListBox.SelectedIndex);
        }
    }

    private void PlaylistListBox_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (paths is not null)
            {
                EnqueuePaths(paths, playFirstNewItem: _playlist.Count == 0);
            }
        }
    }

    private void LibraryTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem node && node.Tag is string path && Directory.Exists(path))
        {
            _currentFolderPath = path;
        }
    }

    private void LibraryTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LibraryTreeView.SelectedItem is not TreeViewItem node || node.Tag is not string path)
        {
            return;
        }

        if (File.Exists(path))
        {
            EnqueuePaths([path], playFirstNewItem: true);
        }
        else if (Directory.Exists(path))
        {
            EnqueuePaths([path], playFirstNewItem: _playlist.Count == 0);
        }
    }

    private void PinCurrentFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFolderPath) || !Directory.Exists(_currentFolderPath))
        {
            PlaybackStatusText.Text = "Open or browse a folder first.";
            return;
        }

        if (!_appSettings.PinnedRoots.Contains(_currentFolderPath, StringComparer.OrdinalIgnoreCase))
        {
            _appSettings.PinnedRoots.Add(_currentFolderPath);
            AppStateStore.SaveSettings(_appSettings);
            RebuildLibraryTree();
        }
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (paths is not null)
        {
            EnqueuePaths(paths, playFirstNewItem: _playlist.Count == 0);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (MatchesShortcut(e, "play_pause"))
        {
            PlayPauseButton_Click(sender, new RoutedEventArgs());
        }
        else if (MatchesShortcut(e, "seek_back_small"))
        {
            SeekBy(TimeSpan.FromSeconds(-5));
        }
        else if (MatchesShortcut(e, "seek_forward_small"))
        {
            SeekBy(TimeSpan.FromSeconds(5));
        }
        else if (MatchesShortcut(e, "seek_back_large"))
        {
            SeekBy(TimeSpan.FromSeconds(-15));
        }
        else if (MatchesShortcut(e, "seek_forward_large"))
        {
            SeekBy(TimeSpan.FromSeconds(15));
        }
        else if (MatchesShortcut(e, "previous_frame"))
        {
            PreviousFrameButton_Click(sender, new RoutedEventArgs());
        }
        else if (MatchesShortcut(e, "next_frame"))
        {
            NextFrameButton_Click(sender, new RoutedEventArgs());
        }
        else if (MatchesShortcut(e, "fullscreen"))
        {
            FullscreenButton_Click(sender, new RoutedEventArgs());
        }
        else if (MatchesShortcut(e, "pip"))
        {
            PiPButton_Click(sender, new RoutedEventArgs());
        }
        else if (MatchesShortcut(e, "mute"))
        {
            MuteButton_Click(sender, new RoutedEventArgs());
        }
        else if (MatchesShortcut(e, "subtitle_delay_back"))
        {
            AdjustSubtitleDelay(-0.05);
        }
        else if (MatchesShortcut(e, "subtitle_delay_forward"))
        {
            AdjustSubtitleDelay(0.05);
        }
        else if (MatchesShortcut(e, "audio_delay_back"))
        {
            AdjustAudioDelay(-0.05);
        }
        else if (MatchesShortcut(e, "audio_delay_forward"))
        {
            AdjustAudioDelay(0.05);
        }
        else if (MatchesShortcut(e, "speed_up"))
        {
            AdjustPlaybackRate(0.25);
        }
        else if (MatchesShortcut(e, "speed_down"))
        {
            AdjustPlaybackRate(-0.25);
        }
        else if (MatchesShortcut(e, "speed_reset"))
        {
            SetPlaybackRate(1.0);
        }
        else if (MatchesShortcut(e, "next_item"))
        {
            NextPlaylistButton_Click(sender, new RoutedEventArgs());
        }
        else if (MatchesShortcut(e, "previous_item"))
        {
            PreviousPlaylistButton_Click(sender, new RoutedEventArgs());
        }
        else if (MatchesShortcut(e, "subtitle_toggle"))
        {
            ShowSubtitlesMenuItem.IsChecked = ShowSubtitlesMenuItem.IsChecked != true;
        }
        else if (MatchesShortcut(e, "translation_toggle"))
        {
            EnableTranslationForCurrentVideoMenuItem.IsChecked = EnableTranslationForCurrentVideoMenuItem.IsChecked != true;
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private bool MatchesShortcut(KeyEventArgs e, string commandId)
    {
        if (!_appSettings.ShortcutProfile.Bindings.TryGetValue(commandId, out var gestureText) || string.IsNullOrWhiteSpace(gestureText))
        {
            return false;
        }

        return TryParseShortcut(gestureText, out var modifiers, out var key)
            && Keyboard.Modifiers == modifiers
            && e.Key == key;
    }

    private static bool TryParseShortcut(string gestureText, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;

        var parts = gestureText.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (var part in parts[..^1])
        {
            if (!Enum.TryParse<ModifierKeys>(part, true, out var modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        return Enum.TryParse(parts[^1], true, out key);
    }

    private void AdjustPlaybackRate(double delta)
    {
        var rate = Math.Clamp((Player.PlaybackRate <= 0 ? 1.0 : Player.PlaybackRate) + delta, 0.25, 2.0);
        SetPlaybackRate(rate);
    }

    private void ApplyPlaybackSpeedToCombo(double speed)
    {
        foreach (var item in PlaybackSpeedComboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && double.TryParse(tag, out var itemSpeed) && Math.Abs(itemSpeed - speed) < 0.01)
            {
                PlaybackSpeedComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private async void OpenSubtitlesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Subtitle files|*.srt;*.vtt;*.ass;*.ssa|SubRip subtitles|*.srt|WebVTT|*.vtt|ASS/SSA|*.ass;*.ssa|All files|*.*",
            Title = "Choose subtitles"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        CancelCaptionGeneration();
        await LoadSubtitlesFromPathAsync(dialog.FileName, autoLoaded: false);
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_subtitleManager.HasCues)
        {
            PlaybackStatusText.Text = "No subtitles available to export.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "SubRip subtitles|*.srt",
            FileName = "translated-subtitles.srt",
            Title = "Save translated subtitles"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _subtitleManager.ExportSrt(dialog.FileName);
        PlaybackStatusText.Text = $"Exported translated subtitles: {Path.GetFileName(dialog.FileName)}";
    }

    private async Task<bool> TryLoadSidecarSubtitlesAsync(string videoPath)
    {
        var sidecarPath = Path.ChangeExtension(videoPath, ".srt");
        if (!File.Exists(sidecarPath))
        {
            _subtitleManager.Clear();
            _subtitleSource = SubtitlePipelineSource.None;
            SetOverlayStatus("No sidecar subtitles found. Generating captions from the video audio.");
            return false;
        }

        await LoadSubtitlesFromPathAsync(sidecarPath, autoLoaded: true);
        return true;
    }

    private async Task LoadSubtitlesFromPathAsync(string path, bool autoLoaded)
    {
        IReadOnlyList<SubtitleCue> cues;
        try
        {
            cues = await SubtitleImportService.LoadExternalSubtitleCuesAsync(
                path,
                HandleFfmpegRuntimeInstallProgress,
                message =>
                {
                    PlaybackStatusText.Text = message;
                    SetOverlayStatus(message);
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            PlaybackStatusText.Text = ex.Message;
            SetOverlayStatus("Subtitle import failed.");
            return;
        }

        await LoadSubtitleCuesAsync(
            cues,
            autoLoaded ? SubtitlePipelineSource.Sidecar : SubtitlePipelineSource.Manual,
            autoLoaded
                ? $"Loaded sidecar subtitles: {Path.GetFileName(path)}"
                : $"Loaded subtitles: {Path.GetFileName(path)}");
    }

    private async Task LoadSubtitleCuesAsync(IReadOnlyList<SubtitleCue> cues, SubtitlePipelineSource source, string statusPrefix)
    {
        InitializeTranslationPreferencesForNewVideo();
        _subtitleManager.LoadCues(cues);
        _subtitleSource = source;
        _currentSourceLanguage = "und";

        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = null;
        lock (_translationSync)
        {
            _inFlightCueTranslations.Clear();
        }

        if (!_subtitleManager.HasCues)
        {
            PlaybackStatusText.Text = "No playable subtitle cues were found.";
            SetOverlayStatus("Loaded subtitle file contains no playable cues.");
            return;
        }

        _currentSourceLanguage = ApplySourceLanguageToCues(_subtitleManager.Cues);
        ApplyAutomaticTranslationPreferenceIfNeeded();

        PlaybackStatusText.Text = $"{statusPrefix} ({_subtitleManager.CueCount} cues).";

        SetOverlayStatus(_isTranslationEnabledForCurrentVideo
            ? "Preparing translated subtitles..."
            : "Preparing source-language subtitles...");

        var cts = new CancellationTokenSource();
        _translationCts = cts;
        _ = TranslateAllCuesAsync(cts.Token);
    }

    private async Task LoadEmbeddedSubtitleTrackAsync(MediaTrackInfo track)
    {
        if (string.IsNullOrWhiteSpace(_currentVideoPath))
        {
            PlaybackStatusText.Text = "Open a video first.";
            return;
        }

        CancelCaptionGeneration();
        Player.SelectSubtitleTrack(null);

        try
        {
            var cues = await SubtitleImportService.ExtractEmbeddedSubtitleCuesAsync(
                _currentVideoPath,
                track,
                HandleFfmpegRuntimeInstallProgress,
                message =>
                {
                    PlaybackStatusText.Text = message;
                    SetOverlayStatus(message);
                },
                CancellationToken.None);

            await LoadSubtitleCuesAsync(cues, SubtitlePipelineSource.EmbeddedTrack, $"Imported embedded subtitle track {track.Id}");
        }
        catch (Exception ex)
        {
            PlaybackStatusText.Text = ex.Message;
            SetOverlayStatus("Embedded subtitle import failed.");
        }
    }

    private async Task StartAutomaticCaptionGenerationAsync(string videoPath)
    {
        CancelCaptionGeneration();
        _subtitleManager.Clear();
        _currentSourceLanguage = "und";
        InitializeTranslationPreferencesForNewVideo();
        _subtitleSource = SubtitlePipelineSource.Generated;
        _isCaptionGenerationInProgress = true;
        _autoResumePlaybackAfterCaptionReady = Player.Source is not null && Player.Position <= TimeSpan.FromSeconds(2);

        var generationId = Interlocked.Increment(ref _activeCaptionGenerationId);
        var cts = new CancellationTokenSource();
        _captionGenerationCts = cts;

        var transcriptionModel = GetSelectedTranscriptionModel();
        var apiKey = _sessionOpenAiApiKey;
        var mode = transcriptionModel.Provider == TranscriptionProvider.Cloud && !string.IsNullOrWhiteSpace(apiKey)
            ? CaptionTranscriptionMode.Cloud
            : CaptionTranscriptionMode.Local;

        var statusText = mode == CaptionTranscriptionMode.Cloud
            ? $"Generating captions with {transcriptionModel.DisplayName}."
            : $"Generating captions with {transcriptionModel.DisplayName}.";
        _captionGenerationModeLabel = transcriptionModel.DisplayName;

        if (transcriptionModel.Provider == TranscriptionProvider.Cloud && string.IsNullOrWhiteSpace(apiKey))
        {
            statusText = "OpenAI API key is missing. Reverting to local transcription.";
            _selectedSubtitleModelKey = DefaultSubtitleModelKey;
            UpdateSubtitleModelMenuChecks();
            transcriptionModel = GetSelectedTranscriptionModel();
            mode = CaptionTranscriptionMode.Local;
            _captionGenerationModeLabel = transcriptionModel.DisplayName;
        }

        PlaybackStatusText.Text = statusText;
        SetOverlayStatus(_isTranslationEnabledForCurrentVideo
            ? "Listening to the video audio and building translated captions..."
            : "Listening to the video audio and building subtitles...");

        var asrService = new AsrService();
        asrService.OnFinal += chunk => HandleRecognizedChunk(chunk, generationId);
        asrService.OnModelTransferProgress += progress => HandleSubtitleModelTransferProgress(progress, generationId);

        try
        {
            var cues = await asrService.TranscribeVideoAsync(
                videoPath,
                new CaptionGenerationOptions
                {
                    Mode = mode,
                    LanguageHint = null,
                    OpenAiApiKey = apiKey,
                    LocalModelType = transcriptionModel.LocalModelType,
                    CloudModel = transcriptionModel.CloudModel
                },
                cts.Token);

            if (generationId != _activeCaptionGenerationId || cts.IsCancellationRequested)
            {
                return;
            }

            _isCaptionGenerationInProgress = false;
            _autoResumePlaybackAfterCaptionReady = false;

            PlaybackStatusText.Text = cues.Count > 0
                ? $"Generated {cues.Count} caption cues automatically."
                : "No speech could be recognized from the video audio.";
            if (cues.Count == 0)
            {
                SetOverlayStatus("No speech could be recognized from the video audio.");
            }
        }
        catch (OperationCanceledException)
        {
            _isCaptionGenerationInProgress = false;
            _autoResumePlaybackAfterCaptionReady = false;
        }
        catch (Exception ex)
        {
            if (generationId != _activeCaptionGenerationId)
            {
                return;
            }

            _isCaptionGenerationInProgress = false;
            _autoResumePlaybackAfterCaptionReady = false;

            PlaybackStatusText.Text = $"Automatic caption generation failed: {ex.Message}";
            SetOverlayStatus("Automatic caption generation failed. You can still load a manual .srt file.");
        }
    }

    private async void SetOpenAiApiKeyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = PromptForApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        await SaveApiKeyAsync(apiKey);
    }

    private async void SubtitleModelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string modelKey)
        {
            return;
        }

        await SelectSubtitleModelAsync(modelKey);
    }

    private async void TranslationModelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string modelKey)
        {
            return;
        }

        await SelectTranslationModelAsync(modelKey);
    }

    private async Task TranslateAllCuesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_isTranslationEnabledForCurrentVideo && _translator.UseCloudTranslation)
            {
                var cues = _subtitleManager.Cues.ToList();
                var translatedTexts = await TranslateCueBatchAsync(cues, cancellationToken);
                for (var index = 0; index < cues.Count; index++)
                {
                    lock (_translationSync)
                    {
                        _subtitleManager.CommitTranslation(cues[index], translatedTexts[index]);
                    }
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    var activeCue = _subtitleManager.GetCueAt(Player.Position);
                    if (activeCue is not null)
                    {
                        SetOverlayStatus(null);
                        RefreshSubtitleOverlay();
                    }
                });
            }
            else
            {
                foreach (var cue in _subtitleManager.Cues.ToList())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await TranslateCueAsync(cue, cancellationToken);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    PlaybackStatusText.Text = _isTranslationEnabledForCurrentVideo
                        ? $"Prepared {_subtitleManager.CueCount} translated subtitle cues."
                        : $"Prepared {_subtitleManager.CueCount} source-language subtitle cues.";
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await HandleCloudServiceFailureAsync(ex);
        }
    }

    private void HandleRecognizedChunk(TranscriptChunk chunk, int generationId)
    {
        if (generationId != _activeCaptionGenerationId || string.IsNullOrWhiteSpace(chunk.Text))
        {
            return;
        }

        var cue = new SubtitleCue
        {
            Start = TimeSpan.FromSeconds(chunk.StartTimeSec),
            End = TimeSpan.FromSeconds(chunk.EndTimeSec),
            SourceText = chunk.Text.Trim(),
            SourceLanguage = ResolveSourceLanguage(chunk.Text)
        };

        _currentSourceLanguage = ResolveAggregateSourceLanguage(_currentSourceLanguage, cue.SourceLanguage);
        ApplyAutomaticTranslationPreferenceIfNeeded();

        lock (_translationSync)
        {
            _subtitleManager.AddCue(cue);
        }

        var cueCount = _subtitleManager.CueCount;
        Dispatcher.Invoke(() =>
        {
            var progressTail = Player.NaturalDuration.HasTimeSpan
                ? $"{FormatClock(cue.End)} / {FormatClock(Player.NaturalDuration.TimeSpan)}"
                : FormatClock(cue.End);
            PlaybackStatusText.Text = $"Generating captions ({_captionGenerationModeLabel})... {cueCount} cues, {progressTail}.";
        });

        if (_autoResumePlaybackAfterCaptionReady)
        {
            _autoResumePlaybackAfterCaptionReady = false;
            Dispatcher.Invoke(() =>
            {
                Player.Position = TimeSpan.Zero;
                Player.Play();
                _isPaused = false;
                PlayPauseButton.Content = "⏸";
                PlaybackStatusText.Text = "Captions ready. Playing with generated subtitles.";
                UpdateTransportPosition();
            });
        }

        SetOverlayStatus(null);
        _ = TranslateCueAsync(cue, _captionGenerationCts?.Token ?? CancellationToken.None);

        Dispatcher.Invoke(() =>
        {
            if (Player.Position >= cue.Start && Player.Position <= cue.End)
            {
                RefreshSubtitleOverlay();
            }
        });
    }

    private bool HasSelectedTranslationModel()
    {
        return !string.IsNullOrWhiteSpace(_selectedTranslationModelKey);
    }

    private void PublishLocalTranslationPreparationStatus()
    {
        if (!HasSelectedTranslationModel())
        {
            return;
        }

        var selection = GetSelectedTranslationModel();
        string? message = selection.Provider switch
        {
            TranslationProvider.LocalHyMt15_1_8B => "Starting HY-MT1.5 1.8B local translation. First use may download and load the model through llama.cpp.",
            TranslationProvider.LocalHyMt15_7B => "Starting HY-MT1.5 7B local translation. First use may download and load the model through llama.cpp.",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(_overlayStatusText))
        {
            Dispatcher.Invoke(() =>
            {
                PlaybackStatusText.Text = message;
                SetOverlayStatus(message);
            });
        }
    }

    private void SubtitleTimer_Tick(object? sender, EventArgs e)
    {
        UpdateTransportPosition();

        if (!_subtitleManager.HasCues)
        {
            if (_isCaptionGenerationInProgress)
            {
                SetOverlayStatus("Generating captions. Playback will start automatically when the first cue is ready.");
            }

            return;
        }

        var cue = _subtitleManager.GetCueAt(Player.Position);
        if (cue is null)
        {
            RefreshSubtitleOverlay();
            return;
        }

        if (string.IsNullOrWhiteSpace(cue.TranslatedText))
        {
            _ = TranslateCueAsync(cue, _translationCts?.Token ?? CancellationToken.None);
        }

        RefreshSubtitleOverlay();
    }

    private static string GetVideoFileDialogFilter()
    {
        return "Video files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm;*.m4v|All files|*.*";
    }

    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v"
    };

    private void LoadPlaybackSettings()
    {
        _audioDelaySeconds = _appSettings.AudioDelaySeconds;
        _subtitleDelaySeconds = _appSettings.SubtitleDelaySeconds;
        _selectedAspectRatio = string.IsNullOrWhiteSpace(_appSettings.AspectRatioOverride) ? "auto" : _appSettings.AspectRatioOverride;
        VolumeSlider.Value = 0.8;
        ApplyPlaybackSpeedToCombo(_appSettings.DefaultPlaybackRate);
        ApplySubtitleStyleSettings();
        UpdateSubtitleRenderModeMenuChecks();
        UpdateHardwareDecodingMenuChecks();
        UpdateAspectRatioMenuChecks();
        ApplySidePanelVisibility();
        UpdatePanelVisibilityMenuChecks();
    }

    private void PersistPlaybackSettings()
    {
        _appSettings = _appSettings with
        {
            DefaultPlaybackRate = Player.PlaybackRate <= 0 ? 1.0 : Player.PlaybackRate,
            AudioDelaySeconds = _audioDelaySeconds,
            SubtitleDelaySeconds = _subtitleDelaySeconds,
            AspectRatioOverride = _selectedAspectRatio,
            ShowBrowserPanel = _appSettings.ShowBrowserPanel,
            ShowPlaylistPanel = _appSettings.ShowPlaylistPanel,
            WindowMode = GetCurrentWindowMode()
        };
        AppStateStore.SaveSettings(_appSettings);
    }

    private PlaybackWindowMode GetCurrentWindowMode()
    {
        if (Topmost && WindowStyle == WindowStyle.None && Width <= 520 && Height <= 360)
        {
            return PlaybackWindowMode.PictureInPicture;
        }

        if (WindowStyle == WindowStyle.None)
        {
            return PlaybackWindowMode.Borderless;
        }

        return PlaybackWindowMode.Standard;
    }

    private void ApplyWindowMode(PlaybackWindowMode mode, bool persist = true)
    {
        switch (mode)
        {
            case PlaybackWindowMode.PictureInPicture:
                Topmost = true;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;
                Width = 480;
                Height = 270;
                Left = SystemParameters.WorkArea.Right - Width - 24;
                Top = SystemParameters.WorkArea.Bottom - Height - 24;
                PiPButton.Content = "❐";
                FullscreenButton.Content = "⛶";
                break;
            case PlaybackWindowMode.Borderless:
                Topmost = false;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.CanResize;
                WindowState = WindowState.Normal;
                PiPButton.Content = "▣";
                FullscreenButton.Content = "⛶";
                break;
            default:
                Topmost = false;
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                if (WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                }

                Width = Math.Max(Width, 960);
                Height = Math.Max(Height, 600);
                PiPButton.Content = "▣";
                FullscreenButton.Content = "⛶";
                break;
        }

        if (persist)
        {
            PersistPlaybackSettings();
        }
    }

    private void AdjustSubtitleDelay(double delta)
    {
        _subtitleDelaySeconds += delta;
        Player.SetSubtitleDelay(_subtitleDelaySeconds);
        PlaybackStatusText.Text = $"Subtitle delay: {_subtitleDelaySeconds:+0.00;-0.00;0.00}s";
        PersistPlaybackSettings();
    }

    private void ResetSubtitleDelay()
    {
        _subtitleDelaySeconds = 0;
        Player.SetSubtitleDelay(_subtitleDelaySeconds);
        PlaybackStatusText.Text = "Subtitle delay reset.";
        PersistPlaybackSettings();
    }

    private void AdjustAudioDelay(double delta)
    {
        _audioDelaySeconds += delta;
        Player.SetAudioDelay(_audioDelaySeconds);
        PlaybackStatusText.Text = $"Audio delay: {_audioDelaySeconds:+0.00;-0.00;0.00}s";
        PersistPlaybackSettings();
    }

    private void ResetAudioDelay()
    {
        _audioDelaySeconds = 0;
        Player.SetAudioDelay(_audioDelaySeconds);
        PlaybackStatusText.Text = "Audio delay reset.";
        PersistPlaybackSettings();
    }

    private void SetPlaybackRate(double speed)
    {
        Player.SetPlaybackRate(speed);
        ApplyPlaybackSpeedToCombo(speed);
        _appSettings = _appSettings with { DefaultPlaybackRate = speed };
        AppStateStore.SaveSettings(_appSettings);
    }

    private void ApplySubtitleStyleSettings()
    {
        if (SubtitleOverlayContainer is null)
        {
            return;
        }

        var style = _appSettings.SubtitleStyle;
        SubtitleSourceText.FontSize = style.SourceFontSize;
        SubtitleText.FontSize = style.TranslationFontSize;
        SubtitleSourceText.Foreground = ParseBrush(style.SourceForegroundHex, "#CCDAE8");
        SubtitleText.Foreground = ParseBrush(style.TranslationForegroundHex, "#F7FBFF");
        SubtitleSourceText.Margin = new Thickness(0, 0, 0, style.DualSpacing);
        SubtitleOverlayContainer.Margin = new Thickness(0, 0, 0, style.BottomMargin);
        SubtitleOverlayContainer.Background = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Clamp(Math.Round(style.BackgroundOpacity * 255), 0, 255),
            0x14,
            0x1C,
            0x25));
    }

    private void ApplySidePanelVisibility()
    {
        if (BrowserColumn is null || PlaylistColumn is null)
        {
            return;
        }

        BrowserColumn.Width = _appSettings.ShowBrowserPanel ? new GridLength(BrowserPanelWidth) : new GridLength(0);
        PlaylistColumn.Width = _appSettings.ShowPlaylistPanel ? new GridLength(PlaylistPanelWidth) : new GridLength(0);
        if (BrowserPanel is not null)
        {
            BrowserPanel.Visibility = _appSettings.ShowBrowserPanel ? Visibility.Visible : Visibility.Collapsed;
        }

        if (PlaylistPanel is not null)
        {
            PlaylistPanel.Visibility = _appSettings.ShowPlaylistPanel ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdatePanelVisibilityMenuChecks()
    {
        if (ShowBrowserPanelMenuItem is not null)
        {
            ShowBrowserPanelMenuItem.IsChecked = _appSettings.ShowBrowserPanel;
        }

        if (ShowPlaylistPanelMenuItem is not null)
        {
            ShowPlaylistPanelMenuItem.IsChecked = _appSettings.ShowPlaylistPanel;
        }
    }

    private static Brush ParseBrush(string? hex, string fallbackHex)
    {
        var converter = new BrushConverter();
        return converter.ConvertFromString(string.IsNullOrWhiteSpace(hex) ? fallbackHex : hex) as Brush
            ?? Brushes.White;
    }

    private void EnqueuePaths(IEnumerable<string> paths, bool playFirstNewItem)
    {
        var addedIndices = new List<int>();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (Directory.Exists(path))
            {
                addedIndices.AddRange(AddDirectoryToPlaylist(path));
            }
            else if (File.Exists(path) && SupportedVideoExtensions.Contains(Path.GetExtension(path)))
            {
                var index = AddPlaylistItem(path);
                if (index >= 0)
                {
                    addedIndices.Add(index);
                }
            }
        }

        if (playFirstNewItem && addedIndices.Count > 0)
        {
            _ = PlayPlaylistIndexAsync(addedIndices[0]);
        }
    }

    private IEnumerable<int> AddDirectoryToPlaylist(string folderPath)
    {
        var added = new List<int>();
        foreach (var file in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                     .Where(file => SupportedVideoExtensions.Contains(Path.GetExtension(file)))
                     .OrderBy(file => file, StringComparer.CurrentCultureIgnoreCase))
        {
            var index = AddPlaylistItem(file);
            if (index >= 0)
            {
                added.Add(index);
            }
        }

        _currentFolderPath = folderPath;
        RebuildLibraryTree();
        return added;
    }

    private int AddPlaylistItem(string path)
    {
        if (_playlist.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return _playlist.ToList().FindIndex(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        var item = new PlaylistItem
        {
            Path = path,
            DisplayName = Path.GetFileName(path)
        };
        _playlist.Add(item);
        return _playlist.Count - 1;
    }

    private async Task PlayPlaylistIndexAsync(int index)
    {
        if (index < 0 || index >= _playlist.Count)
        {
            return;
        }

        _playlistIndex = index;
        PlaylistListBox.SelectedIndex = index;
        var item = _playlist[index];
        await OpenVideoPathAsync(item.Path);
    }

    private async Task OpenVideoPathAsync(string path)
    {
        CancelBackgroundWork();
        _currentVideoPath = path;
        _subtitleSource = SubtitlePipelineSource.None;
        InitializeTranslationPreferencesForNewVideo();

        Player.Source = new Uri(path);
        Player.Play();

        _isPaused = false;
        PlayPauseButton.Content = "⏸";
        PlaybackStatusText.Text = $"Playing: {Path.GetFileName(path)}";

        var hasSubtitles = await TryLoadSidecarSubtitlesAsync(path);
        if (!hasSubtitles)
        {
            Player.Pause();
            _isPaused = true;
            PlayPauseButton.Content = "▶";
            _ = StartAutomaticCaptionGenerationAsync(path);
        }
    }

    private void ResumeTimer_Tick(object? sender, EventArgs e)
    {
        SaveResumePosition();
    }

    private void SaveResumePosition(bool forceRemoveCompleted = false)
    {
        if (string.IsNullOrWhiteSpace(_currentVideoPath) || !_appSettings.ResumeEnabled)
        {
            return;
        }

        var duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        if (duration <= TimeSpan.FromMinutes(2))
        {
            return;
        }

        var position = Player.Position;
        var completionRatio = duration.TotalSeconds <= 0 ? 0 : position.TotalSeconds / duration.TotalSeconds;
        _resumeEntries.RemoveAll(entry => string.Equals(entry.Path, _currentVideoPath, StringComparison.OrdinalIgnoreCase));

        if (forceRemoveCompleted || completionRatio >= 0.95)
        {
            AppStateStore.SaveResumeEntries(_resumeEntries);
            return;
        }

        if (position < TimeSpan.FromMinutes(2))
        {
            AppStateStore.SaveResumeEntries(_resumeEntries);
            return;
        }

        _resumeEntries.Add(new PlaybackResumeEntry
        {
            Path = _currentVideoPath!,
            PositionSeconds = position.TotalSeconds,
            DurationSeconds = duration.TotalSeconds,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        AppStateStore.SaveResumeEntries(_resumeEntries);
    }

    private void TryApplyResumePosition()
    {
        if (string.IsNullOrWhiteSpace(_currentVideoPath) || !Player.NaturalDuration.HasTimeSpan || !_appSettings.ResumeEnabled)
        {
            return;
        }

        var entry = _resumeEntries
            .Where(item => string.Equals(item.Path, _currentVideoPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (entry is null)
        {
            return;
        }

        var duration = Player.NaturalDuration.TimeSpan;
        if (entry.PositionSeconds < TimeSpan.FromMinutes(2).TotalSeconds)
        {
            return;
        }

        if (entry.PositionSeconds >= duration.TotalSeconds * 0.95)
        {
            return;
        }

        Player.Position = TimeSpan.FromSeconds(Math.Clamp(entry.PositionSeconds, 0, duration.TotalSeconds));
        UpdateTransportPosition();
        PlaybackStatusText.Text = $"Resumed: {Path.GetFileName(_currentVideoPath)}";
    }

    private void RebuildLibraryTree()
    {
        if (LibraryTreeView is null)
        {
            return;
        }

        LibraryTreeView.Items.Clear();

        var roots = new List<string>();
        roots.AddRange(_appSettings.PinnedRoots.Where(Directory.Exists));
        if (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath) && !roots.Contains(_currentFolderPath, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(_currentFolderPath);
        }

        if (roots.Count == 0)
        {
            roots.AddRange(DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName));
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            LibraryTreeView.Items.Add(CreateDirectoryNode(root, expand: string.Equals(root, _currentFolderPath, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private TreeViewItem CreateDirectoryNode(string path, bool expand = false)
    {
        var node = new TreeViewItem
        {
            Header = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path,
            Tag = path,
            IsExpanded = expand,
            Foreground = System.Windows.Media.Brushes.White
        };
        node.Items.Add("*");
        node.Expanded += DirectoryNode_Expanded;
        return node;
    }

    private void DirectoryNode_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem node || node.Tag is not string path || !Directory.Exists(path))
        {
            return;
        }

        if (node.Items.Count == 1 && Equals(node.Items[0], "*"))
        {
            node.Items.Clear();
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(path).OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase))
                {
                    node.Items.Add(CreateDirectoryNode(directory));
                }

                foreach (var file in Directory.EnumerateFiles(path)
                             .Where(file => SupportedVideoExtensions.Contains(Path.GetExtension(file)))
                             .OrderBy(file => file, StringComparer.CurrentCultureIgnoreCase))
                {
                    node.Items.Add(new TreeViewItem
                    {
                        Header = Path.GetFileName(file),
                        Tag = file,
                        Foreground = System.Windows.Media.Brushes.White
                    });
                }
            }
            catch
            {
            }
        }
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        Player.SetAudioDelay(_audioDelaySeconds);
        Player.SetSubtitleDelay(_subtitleDelaySeconds);
        Player.SetAspectRatio(_selectedAspectRatio);
        UpdateTransportDuration();
        UpdateTransportPosition();
        UpdateTransportVisibility();
        TryApplyResumePosition();
        _subtitleTimer.Start();
        _resumeTimer.Start();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        _subtitleTimer.Stop();
        _resumeTimer.Stop();
        _isPaused = true;
        PlayPauseButton.Content = "▶";
        if (Player.NaturalDuration.HasTimeSpan)
        {
            Player.Position = Player.NaturalDuration.TimeSpan;
        }

        SaveResumePosition(forceRemoveCompleted: true);
        UpdateTransportPosition();
        UpdateTransportVisibility();
        if (_playlistIndex + 1 < _playlist.Count)
        {
            _ = PlayPlaylistIndexAsync(_playlistIndex + 1);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        CancelBackgroundWork();
        _subtitleTimer.Stop();
        _resumeTimer.Stop();
        SaveResumePosition();
        PersistPlaybackSettings();
    }

    private void CancelBackgroundWork()
    {
        CancelCaptionGeneration();

        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = null;
        lock (_translationSync)
        {
            _inFlightCueTranslations.Clear();
        }
    }

    private void CancelCaptionGeneration()
    {
        Interlocked.Increment(ref _activeCaptionGenerationId);
        _isCaptionGenerationInProgress = false;
        _autoResumePlaybackAfterCaptionReady = false;
        _captionGenerationCts?.Cancel();
        _captionGenerationCts?.Dispose();
        _captionGenerationCts = null;
    }

    private async Task<bool> SaveApiKeyAsync(string apiKey)
    {
        try
        {
            PlaybackStatusText.Text = "Validating OpenAI API key...";
            await MtService.ValidateApiKeyAsync(apiKey, CancellationToken.None);

            _sessionOpenAiApiKey = apiKey.Trim();
            SecureSettingsStore.SaveOpenAiApiKey(_sessionOpenAiApiKey);
            ConfigureTranslator();
            PlaybackStatusText.Text = "OpenAI API key saved and verified.";
            return true;
        }
        catch (Exception ex)
        {
            PlaybackStatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Invalid OpenAI API Key", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void ResetCurrentTranslations()
    {
        lock (_translationSync)
        {
            foreach (var cue in _subtitleManager.Cues)
            {
                cue.TranslatedText = null;
            }

            _inFlightCueTranslations.Clear();
        }
    }

    private void ConfigureTranslator()
    {
        if (!HasSelectedTranslationModel())
        {
            _translator.ConfigureLocal(new LocalTranslationOptions(OfflineTranslationModel.None));
            _translator.ConfigureCloud(null);
            return;
        }

        var selectedTranslationModel = GetSelectedTranslationModel();
        _translator.ConfigureLocal(GetLocalTranslationOptions(selectedTranslationModel));
        _translator.ConfigureCloud(GetCloudTranslationOptions(selectedTranslationModel));
    }

    private string? PromptForApiKey()
    {
        var dialog = new ApiKeyPromptWindow(
            "OpenAI API Key",
            "Enter an OpenAI API key. This key is used for cloud subtitle transcription and OpenAI translation, and it is saved securely for future sessions.",
            "Use Key")
        {
            Owner = this
        };

        var accepted = dialog.ShowDialog();
        return accepted == true && !string.IsNullOrWhiteSpace(dialog.ApiKey)
            ? dialog.ApiKey.Trim()
            : null;
    }

    private async void SetGoogleTranslateApiKeyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await PromptAndSaveGoogleTranslateCredentialsAsync();
    }

    private async void SetDeepLApiKeyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await PromptAndSaveDeepLCredentialsAsync();
    }

    private async void SetMicrosoftTranslatorCredentialsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await PromptAndSaveMicrosoftTranslatorCredentialsAsync();
    }

    private void SetLlamaCppServerPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var path = PromptForLlamaCppServerPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        SecureSettingsStore.SaveLlamaCppServerPath(path);
        SecureSettingsStore.SaveLlamaCppRuntimeSource("manual");
        ConfigureTranslator();
        PlaybackStatusText.Text = $"Saved llama.cpp server path: {Path.GetFileName(path)}";
    }

    private string? PromptForSingleSecret(string title, string message, string buttonText)
    {
        var dialog = new ApiKeyPromptWindow(title, message, buttonText)
        {
            Owner = this
        };

        var accepted = dialog.ShowDialog();
        return accepted == true && !string.IsNullOrWhiteSpace(dialog.ApiKey)
            ? dialog.ApiKey.Trim()
            : null;
    }

    private CloudTranslationOptions? PromptForMicrosoftTranslatorCredentials()
    {
        var dialog = new ApiKeyWithRegionPromptWindow(
            "Microsoft Translator Credentials",
            "Enter the Microsoft Translator API key and Azure region. Both values are required and are saved for future sessions.",
            "Save Credentials")
        {
            Owner = this
        };

        return dialog.ShowDialog() == true
            ? new CloudTranslationOptions(
                CloudTranslationProvider.MicrosoftTranslator,
                dialog.ApiKey.Trim(),
                null,
                dialog.Region.Trim())
            : null;
    }

    private async Task<bool> SaveCloudTranslationCredentialsAsync(
        CloudTranslationOptions options,
        string providerLabel,
        Action<CloudTranslationOptions> persist)
    {
        try
        {
            PlaybackStatusText.Text = $"Validating {providerLabel} credentials...";
            await MtService.ValidateTranslationProviderAsync(options, CancellationToken.None);
            persist(options);
            if (MapToCloudTranslationProvider(GetSelectedTranslationModel().Provider) == options.Provider)
            {
                ConfigureTranslator();
            }

            PlaybackStatusText.Text = $"{providerLabel} credentials saved and verified.";
            return true;
        }
        catch (Exception ex)
        {
            PlaybackStatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, $"Invalid {providerLabel} Credentials", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async Task<bool> PromptAndSaveGoogleTranslateCredentialsAsync()
    {
        var apiKey = PromptForSingleSecret(
            "Google Translate API Key",
            "Enter a Google Cloud Translation API key. This is used for the Google Translate translation model and is saved securely for future sessions.",
            "Save Key");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        return await SaveCloudTranslationCredentialsAsync(
            new CloudTranslationOptions(CloudTranslationProvider.Google, apiKey),
            "Google Translate",
            options => SecureSettingsStore.SaveGoogleTranslateApiKey(options.ApiKey));
    }

    private async Task<bool> PromptAndSaveDeepLCredentialsAsync()
    {
        var apiKey = PromptForSingleSecret(
            "DeepL API Key",
            "Enter a DeepL API key. The app auto-detects free versus pro endpoints from the key and saves it securely for future sessions.",
            "Save Key");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        return await SaveCloudTranslationCredentialsAsync(
            new CloudTranslationOptions(CloudTranslationProvider.DeepL, apiKey),
            "DeepL",
            options => SecureSettingsStore.SaveDeepLApiKey(options.ApiKey));
    }

    private async Task<bool> PromptAndSaveMicrosoftTranslatorCredentialsAsync()
    {
        var credentials = PromptForMicrosoftTranslatorCredentials();
        if (credentials is null)
        {
            return false;
        }

        return await SaveCloudTranslationCredentialsAsync(
            credentials,
            "Microsoft Translator",
            options =>
            {
                SecureSettingsStore.SaveMicrosoftTranslatorApiKey(options.ApiKey);
                SecureSettingsStore.SaveMicrosoftTranslatorRegion(options.Region ?? string.Empty);
            });
    }

    private CloudTranslationOptions? GetCloudTranslationOptions(TranslationModelSelection selection)
    {
        return selection.Provider switch
        {
            TranslationProvider.OpenAi when !string.IsNullOrWhiteSpace(_sessionOpenAiApiKey)
                => new CloudTranslationOptions(CloudTranslationProvider.OpenAi, _sessionOpenAiApiKey!, selection.CloudModel),
            TranslationProvider.Google => GetGoogleCloudTranslationOptions(),
            TranslationProvider.DeepL => GetDeepLCloudTranslationOptions(),
            TranslationProvider.MicrosoftTranslator => GetMicrosoftCloudTranslationOptions(),
            _ => null
        };
    }

    private static LocalTranslationOptions GetLocalTranslationOptions(TranslationModelSelection selection)
    {
        var llamaServerPath = Environment.GetEnvironmentVariable("LLAMA_SERVER_PATH")
            ?? SecureSettingsStore.GetLlamaCppServerPath();

        return selection.Provider switch
        {
            TranslationProvider.LocalHyMt15_1_8B => new LocalTranslationOptions(OfflineTranslationModel.HyMt15_1_8B, llamaServerPath),
            TranslationProvider.LocalHyMt15_7B => new LocalTranslationOptions(OfflineTranslationModel.HyMt15_7B, llamaServerPath),
            _ => new LocalTranslationOptions(OfflineTranslationModel.None)
        };
    }

    private static CloudTranslationProvider MapToCloudTranslationProvider(TranslationProvider provider)
    {
        return provider switch
        {
            TranslationProvider.OpenAi => CloudTranslationProvider.OpenAi,
            TranslationProvider.Google => CloudTranslationProvider.Google,
            TranslationProvider.DeepL => CloudTranslationProvider.DeepL,
            TranslationProvider.MicrosoftTranslator => CloudTranslationProvider.MicrosoftTranslator,
            _ => CloudTranslationProvider.None
        };
    }

    private static CloudTranslationOptions? GetGoogleCloudTranslationOptions()
    {
        var apiKey = Environment.GetEnvironmentVariable("GOOGLE_TRANSLATE_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_CLOUD_TRANSLATE_API_KEY")
            ?? SecureSettingsStore.GetGoogleTranslateApiKey();
        return string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new CloudTranslationOptions(CloudTranslationProvider.Google, apiKey.Trim());
    }

    private static CloudTranslationOptions? GetDeepLCloudTranslationOptions()
    {
        var apiKey = Environment.GetEnvironmentVariable("DEEPL_API_KEY")
            ?? SecureSettingsStore.GetDeepLApiKey();
        return string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new CloudTranslationOptions(CloudTranslationProvider.DeepL, apiKey.Trim());
    }

    private static CloudTranslationOptions? GetMicrosoftCloudTranslationOptions()
    {
        var apiKey = Environment.GetEnvironmentVariable("MICROSOFT_TRANSLATOR_API_KEY")
            ?? Environment.GetEnvironmentVariable("AZURE_TRANSLATOR_KEY")
            ?? SecureSettingsStore.GetMicrosoftTranslatorApiKey();
        var region = Environment.GetEnvironmentVariable("MICROSOFT_TRANSLATOR_REGION")
            ?? Environment.GetEnvironmentVariable("AZURE_TRANSLATOR_REGION")
            ?? SecureSettingsStore.GetMicrosoftTranslatorRegion();

        return string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(region)
            ? null
            : new CloudTranslationOptions(CloudTranslationProvider.MicrosoftTranslator, apiKey.Trim(), null, region.Trim());
    }

    private async Task<bool> EnsureTranslationProviderCredentialsAsync(TranslationProvider provider)
    {
        switch (provider)
        {
            case TranslationProvider.LocalHyMt15_1_8B:
            case TranslationProvider.LocalHyMt15_7B:
                if (TryResolveLlamaCppServerPath() is not null)
                {
                    return true;
                }
                return await EnsureLlamaCppRuntimeAsync();
            case TranslationProvider.OpenAi:
                return await EnsureOpenAiApiKeyAsync();
            case TranslationProvider.Google:
                if (GetGoogleCloudTranslationOptions() is not null)
                {
                    return true;
                }

                return await PromptAndSaveGoogleTranslateCredentialsAsync();
            case TranslationProvider.DeepL:
                if (GetDeepLCloudTranslationOptions() is not null)
                {
                    return true;
                }

                return await PromptAndSaveDeepLCredentialsAsync();
            case TranslationProvider.MicrosoftTranslator:
                if (GetMicrosoftCloudTranslationOptions() is not null)
                {
                    return true;
                }

                return await PromptAndSaveMicrosoftTranslatorCredentialsAsync();
            default:
                return false;
        }
    }

    private static string? TryResolveLlamaCppServerPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("LLAMA_SERVER_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        configuredPath = SecureSettingsStore.GetLlamaCppServerPath();
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var installedPath = LlamaCppRuntimeInstaller.GetInstalledServerPath();
        if (File.Exists(installedPath))
        {
            return installedPath;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var exePath = Path.Combine(segment, "llama-server.exe");
            if (File.Exists(exePath))
            {
                return exePath;
            }

            var noExtensionPath = Path.Combine(segment, "llama-server");
            if (File.Exists(noExtensionPath))
            {
                return noExtensionPath;
            }
        }

        return null;
    }

    private string? PromptForLlamaCppServerPath()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "llama.cpp server|llama-server.exe;llama-server|Executable files|*.exe|All files|*.*",
            Title = "Choose llama-server"
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private async Task<bool> EnsureLlamaCppRuntimeAsync()
    {
        var setupWindow = new LlamaCppBootstrapWindow
        {
            Owner = this
        };

        _ = setupWindow.ShowDialog();
        switch (setupWindow.Choice)
        {
            case LlamaCppBootstrapChoice.InstallAutomatically:
                return await InstallLlamaCppRuntimeAsync();
            case LlamaCppBootstrapChoice.ChooseExisting:
            {
                var selectedPath = PromptForLlamaCppServerPath();
                if (string.IsNullOrWhiteSpace(selectedPath) || !File.Exists(selectedPath))
                {
                    return false;
                }

                SecureSettingsStore.SaveLlamaCppServerPath(selectedPath);
                SecureSettingsStore.SaveLlamaCppRuntimeSource("manual");
                ConfigureTranslator();
                PlaybackStatusText.Text = $"Using llama.cpp runtime: {Path.GetFileName(selectedPath)}";
                return true;
            }
            case LlamaCppBootstrapChoice.OpenOfficialDownloadPage:
                OpenExternalLink(LlamaCppRuntimeInstaller.ReleasePageUrl);
                PlaybackStatusText.Text = "Opened the official llama.cpp release page.";
                return false;
            default:
                return false;
        }
    }

    private async Task<bool> InstallLlamaCppRuntimeAsync()
    {
        try
        {
            var serverPath = await LlamaCppRuntimeInstaller.InstallAsync(HandleLlamaRuntimeInstallProgress, CancellationToken.None);
            SecureSettingsStore.SaveLlamaCppServerPath(serverPath);
            SecureSettingsStore.SaveLlamaCppRuntimeVersion(LlamaCppRuntimeInstaller.RuntimeVersion);
            SecureSettingsStore.SaveLlamaCppRuntimeSource(LlamaCppRuntimeInstaller.RuntimeSource);
            ConfigureTranslator();
            PlaybackStatusText.Text = "llama.cpp runtime is ready.";
            SetOverlayStatus("llama.cpp runtime is ready.");
            return true;
        }
        catch (Exception ex)
        {
            PlaybackStatusText.Text = $"llama.cpp runtime install failed: {ex.Message}";
            SetOverlayStatus("llama.cpp runtime install failed.");
            return false;
        }
    }

    private async Task TranslateCueAsync(SubtitleCue cue, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(cue.TranslatedText))
        {
            return;
        }

        cue.SourceLanguage ??= ResolveSourceLanguage(cue.SourceText);

        if (ShouldUseTranscriptDirectly(cue))
        {
            lock (_translationSync)
            {
                _subtitleManager.CommitTranslation(cue, cue.SourceText.Trim());
            }

            await Dispatcher.InvokeAsync(() =>
            {
                var activeCue = _subtitleManager.GetCueAt(Player.Position);
                if (ReferenceEquals(activeCue, cue))
                {
                    RefreshSubtitleOverlay();
                }
            });

            return;
        }

        lock (_translationSync)
        {
            if (!string.IsNullOrWhiteSpace(cue.TranslatedText) || !_inFlightCueTranslations.Add(cue))
            {
                return;
            }
        }

        try
        {
            PublishLocalTranslationPreparationStatus();
            var translated = await _translator.TranslateAsync(cue.SourceText, cancellationToken);

            lock (_translationSync)
            {
                _subtitleManager.CommitTranslation(cue, translated);
            }

            await Dispatcher.InvokeAsync(() =>
            {
                var activeCue = _subtitleManager.GetCueAt(Player.Position);
                if (ReferenceEquals(activeCue, cue))
                {
                    RefreshSubtitleOverlay();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await HandleCloudServiceFailureAsync(ex);
        }
        finally
        {
            lock (_translationSync)
            {
                _inFlightCueTranslations.Remove(cue);
            }
        }
    }

    private bool ShouldUseTranscriptDirectly(SubtitleCue cue)
    {
        if (!_isTranslationEnabledForCurrentVideo)
        {
            return true;
        }

        return IsLanguageCode(cue.SourceLanguage ?? _currentSourceLanguage, _translationTargetLanguage);
    }

    private async Task<IReadOnlyList<string>> TranslateCueBatchAsync(IReadOnlyList<SubtitleCue> cues, CancellationToken cancellationToken)
    {
        const int batchSize = 20;
        var translated = new List<string>(cues.Count);

        for (var index = 0; index < cues.Count; index += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = cues.Skip(index).Take(batchSize).ToList();
            var batchTexts = batch.Select(cue => cue.SourceText).ToList();
            var batchTranslations = await _translator.TranslateBatchAsync(batchTexts, cancellationToken);
            translated.AddRange(batchTranslations);
        }

        return translated;
    }

    private string ApplySourceLanguageToCues(IReadOnlyList<SubtitleCue> cues)
    {
        var detectedLanguage = ResolveSourceLanguage(string.Join(" ", cues.Take(6).Select(cue => cue.SourceText)));
        foreach (var cue in cues)
        {
            cue.SourceLanguage ??= detectedLanguage;
        }

        return detectedLanguage;
    }

    private static string ResolveSourceLanguage(string text)
    {
        return LanguageDetector.Detect(text);
    }

    private static string ResolveAggregateSourceLanguage(string currentLanguage, string? nextLanguage)
    {
        if (string.IsNullOrWhiteSpace(nextLanguage) || string.Equals(nextLanguage, "und", StringComparison.OrdinalIgnoreCase))
        {
            return currentLanguage;
        }

        if (string.IsNullOrWhiteSpace(currentLanguage) || string.Equals(currentLanguage, "und", StringComparison.OrdinalIgnoreCase))
        {
            return nextLanguage;
        }

        return currentLanguage;
    }

    private static bool IsEnglishLanguage(string? languageCode)
    {
        return string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase)
            || string.Equals(languageCode, DefaultLocalSpeechCulture, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLanguageCode(string? actualLanguageCode, string? expectedLanguageCode)
    {
        if (string.IsNullOrWhiteSpace(actualLanguageCode) || string.IsNullOrWhiteSpace(expectedLanguageCode))
        {
            return false;
        }

        if (string.Equals(expectedLanguageCode, "en", StringComparison.OrdinalIgnoreCase))
        {
            return IsEnglishLanguage(actualLanguageCode);
        }

        return string.Equals(actualLanguageCode, expectedLanguageCode, StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleCloudServiceFailureAsync(Exception ex)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            if (ShouldDisableCloudForError(ex))
            {
                if (IsCloudTranslationProvider(GetSelectedTranslationModel().Provider))
                {
                    ClearSelectedTranslationModel();
                }

                if (GetSelectedTranscriptionModel().Provider == TranscriptionProvider.Cloud)
                {
                    _selectedSubtitleModelKey = DefaultSubtitleModelKey;
                    UpdateSubtitleModelMenuChecks();
                }

                ConfigureTranslator();
                PlaybackStatusText.Text = "Cloud models were disabled after a quota or rate-limit error.";
                return;
            }

            PlaybackStatusText.Text = ex.Message;
        });
    }

    private static bool ShouldDisableCloudForError(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || message.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCloudTranslationProvider(TranslationProvider provider)
    {
        return provider is TranslationProvider.OpenAi
            or TranslationProvider.Google
            or TranslationProvider.DeepL
            or TranslationProvider.MicrosoftTranslator;
    }

    private void ProgressSlider_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Player.Source is null || !Player.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        _isSeeking = true;
        _resumePlaybackAfterSeek = !_isPaused;
        if (!_isPaused)
        {
            Player.Pause();
        }

        UpdateTransportVisibility();
    }

    private void ProgressSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isSeeking)
        {
            return;
        }

        CommitSeekFromSlider();
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking)
        {
            CurrentTimeText.Text = FormatClock(TimeSpan.FromSeconds(ProgressSlider.Value));
        }
    }

    private void CommitSeekFromSlider()
    {
        if (!Player.NaturalDuration.HasTimeSpan)
        {
            _isSeeking = false;
            _resumePlaybackAfterSeek = false;
            return;
        }

        var duration = Player.NaturalDuration.TimeSpan;
        var target = TimeSpan.FromSeconds(Math.Clamp(ProgressSlider.Value, 0, duration.TotalSeconds));
        Player.Position = target;
        _isSeeking = false;

        if (_resumePlaybackAfterSeek)
        {
            Player.Play();
            _isPaused = false;
            PlayPauseButton.Content = "⏸";
        }
        else
        {
            _isPaused = true;
            PlayPauseButton.Content = "▶";
        }

        _resumePlaybackAfterSeek = false;
        UpdateTransportPosition();
        UpdateTransportVisibility();
    }

    private void SeekBy(TimeSpan delta)
    {
        if (Player.Source is null)
        {
            return;
        }

        var duration = Player.NaturalDuration.HasTimeSpan
            ? Player.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        var target = Player.Position + delta;
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }

        if (duration > TimeSpan.Zero && target > duration)
        {
            target = duration;
        }

        Player.Position = target;
        UpdateTransportPosition();
    }

    private void UpdateTransportDuration()
    {
        var duration = Player.NaturalDuration.HasTimeSpan
            ? Player.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        ProgressSlider.Maximum = Math.Max(1, duration.TotalSeconds);
        DurationText.Text = FormatClock(duration);
    }

    private void UpdateTransportPosition()
    {
        var position = Player.Position;
        if (!_isSeeking)
        {
            ProgressSlider.Value = Math.Min(ProgressSlider.Maximum, Math.Max(0, position.TotalSeconds));
        }

        CurrentTimeText.Text = FormatClock(position);
    }

    private static string FormatClock(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return value.ToString(@"h\:mm\:ss");
        }

        return value.ToString(@"mm\:ss");
    }

    private async void EnableTranslationForCurrentVideoMenuItem_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressTranslationMenuEvents)
        {
            return;
        }

        if (!HasSelectedTranslationModel())
        {
            PlaybackStatusText.Text = "Select a translation model first.";
            SetTranslationEnabledForCurrentVideo(false, reprocessExistingCues: false);
            return;
        }

        _isTranslationEnabledForCurrentVideo = true;
        _currentVideoTranslationPreferenceLocked = true;
        await ReprocessCurrentSubtitlesForTranslationSettingsAsync();
    }

    private async void EnableTranslationForCurrentVideoMenuItem_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressTranslationMenuEvents)
        {
            return;
        }

        _isTranslationEnabledForCurrentVideo = false;
        _currentVideoTranslationPreferenceLocked = true;
        await ReprocessCurrentSubtitlesForTranslationSettingsAsync();
    }

    private void TranslateTargetEnglishMenuItem_Click(object sender, RoutedEventArgs e)
    {
        TranslateTargetEnglishMenuItem.IsChecked = true;
        _translationTargetLanguage = "en";
    }

    private async Task SelectSubtitleModelAsync(string modelKey)
    {
        var previousModelKey = _selectedSubtitleModelKey;
        var selection = GetTranscriptionModel(modelKey);
        if (selection.Provider == TranscriptionProvider.Cloud && !await EnsureOpenAiApiKeyAsync())
        {
            UpdateSubtitleModelMenuChecks(previousModelKey);
            PlaybackStatusText.Text = "Cloud transcription model selection canceled.";
            return;
        }

        _selectedSubtitleModelKey = modelKey;
        SecureSettingsStore.SaveSubtitleModelKey(modelKey);
        UpdateSubtitleModelMenuChecks();
        await ReprocessCurrentSubtitlesForTranscriptionModelAsync(selection);
    }

    private async Task SelectTranslationModelAsync(string modelKey)
    {
        var previousModelKey = _selectedTranslationModelKey;
        var selection = GetTranslationModel(modelKey);
        if (selection.Provider == TranslationProvider.None || !await EnsureTranslationProviderCredentialsAsync(selection.Provider))
        {
            UpdateTranslationModelMenuChecks(previousModelKey);
            PlaybackStatusText.Text = "Translation model selection canceled.";
            return;
        }

        _selectedTranslationModelKey = modelKey;
        SecureSettingsStore.SaveTranslationModelKey(modelKey);
        ConfigureTranslator();
        UpdateTranslationModelMenuChecks();
        if (selection.Provider is TranslationProvider.LocalHyMt15_1_8B or TranslationProvider.LocalHyMt15_7B)
        {
            var warmedUp = await WarmupSelectedLocalTranslationRuntimeAsync(selection);
            if (!warmedUp)
            {
                _selectedTranslationModelKey = previousModelKey;
                if (string.IsNullOrWhiteSpace(previousModelKey))
                {
                    SecureSettingsStore.ClearTranslationModelKey();
                }
                else
                {
                    SecureSettingsStore.SaveTranslationModelKey(previousModelKey);
                }

                ConfigureTranslator();
                UpdateTranslationModelMenuChecks(previousModelKey);
                return;
            }
        }

        _currentVideoTranslationPreferenceLocked = true;
        SetTranslationEnabledForCurrentVideo(true, reprocessExistingCues: false);
        await ReprocessCurrentSubtitlesForTranslationSettingsAsync();
    }

    private async Task<bool> WarmupSelectedLocalTranslationRuntimeAsync(TranslationModelSelection selection)
    {
        try
        {
            PlaybackStatusText.Text = $"Preparing {selection.DisplayName}.";
            SetOverlayStatus($"Preparing {selection.DisplayName}.");
            await _translator.WarmupLocalRuntimeAsync(CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            PlaybackStatusText.Text = ex.Message;
            SetOverlayStatus("Local translation model setup failed.");
            return false;
        }
    }

    private void ClearSelectedTranslationModel()
    {
        _selectedTranslationModelKey = null;
        SecureSettingsStore.ClearTranslationModelKey();
        ConfigureTranslator();
        UpdateTranslationModelMenuChecks();
        SetTranslationEnabledForCurrentVideo(false, reprocessExistingCues: false);
    }

    private async Task<bool> EnsureOpenAiApiKeyAsync()
    {
        if (!string.IsNullOrWhiteSpace(_sessionOpenAiApiKey))
        {
            return true;
        }

        var apiKey = PromptForApiKey();
        return !string.IsNullOrWhiteSpace(apiKey) && await SaveApiKeyAsync(apiKey);
    }

    private async void AutoTranslateNonEnglishMenuItem_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressTranslationMenuEvents)
        {
            return;
        }

        if (!HasSelectedTranslationModel())
        {
            PlaybackStatusText.Text = "Select a translation model before enabling auto-translate.";
            _suppressTranslationMenuEvents = true;
            try
            {
                AutoTranslateNonEnglishMenuItem.IsChecked = false;
            }
            finally
            {
                _suppressTranslationMenuEvents = false;
            }

            return;
        }

        _autoTranslateVideosOutsidePreferredLanguage = true;
        SecureSettingsStore.SaveAutoTranslateEnabled(true);
        _autoTranslatePreferredSourceLanguage = "en";
        _currentVideoTranslationPreferenceLocked = false;
        ApplyAutomaticTranslationPreferenceIfNeeded();
        await ReprocessCurrentSubtitlesForTranslationSettingsAsync();
    }

    private async void AutoTranslateNonEnglishMenuItem_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressTranslationMenuEvents)
        {
            return;
        }

        _autoTranslateVideosOutsidePreferredLanguage = false;
        SecureSettingsStore.SaveAutoTranslateEnabled(false);
        _currentVideoTranslationPreferenceLocked = false;
        ApplyAutomaticTranslationPreferenceIfNeeded();
        await ReprocessCurrentSubtitlesForTranslationSettingsAsync();
    }

    private void ShowSubtitlesMenuItem_Checked(object sender, RoutedEventArgs e)
    {
        RefreshSubtitleOverlay();
    }

    private void ShowSubtitlesMenuItem_Unchecked(object sender, RoutedEventArgs e)
    {
        RefreshSubtitleOverlay();
    }

    private void SetOverlayStatus(string? text)
    {
        _overlayStatusText = text;
        RefreshSubtitleOverlay();
    }

    private void HandleSubtitleModelTransferProgress(ModelTransferProgress progress, int generationId)
    {
        if (generationId != _activeCaptionGenerationId)
        {
            return;
        }

        var message = FormatSubtitleModelTransferStatus(progress);
        Dispatcher.Invoke(() =>
        {
            PlaybackStatusText.Text = message;
            SetOverlayStatus(message);
        });
    }

    private void HandleLocalTranslationRuntimeStatus(LocalTranslationRuntimeStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            PlaybackStatusText.Text = status.Message;
            SetOverlayStatus(status.Message);
        });
    }

    private void HandleLlamaRuntimeInstallProgress(RuntimeInstallProgress progress)
    {
        var message = progress.Stage switch
        {
            "downloading" => progress.ProgressRatio is double ratio
                ? $"Downloading llama.cpp runtime... {ratio:P0} ({FormatBytes(progress.BytesTransferred)} / {FormatBytes(progress.TotalBytes ?? 0)})."
                : $"Downloading llama.cpp runtime... {FormatBytes(progress.BytesTransferred)}.",
            "extracting" => progress.ProgressRatio is double ratio
                ? $"Extracting llama.cpp runtime... {ratio:P0} ({progress.ItemsCompleted ?? 0} / {progress.TotalItems ?? 0})."
                : "Extracting llama.cpp runtime...",
            "ready" => "llama.cpp runtime is ready.",
            _ => "Preparing llama.cpp runtime..."
        };

        Dispatcher.Invoke(() =>
        {
            PlaybackStatusText.Text = message;
            SetOverlayStatus(message);
        });
    }

    private void Player_RuntimeInstallProgress(RuntimeInstallProgress progress)
    {
        var message = progress.Stage switch
        {
            "downloading" => progress.ProgressRatio is double ratio
                ? $"Downloading mpv runtime... {ratio:P0} ({FormatBytes(progress.BytesTransferred)} / {FormatBytes(progress.TotalBytes ?? 0)})."
                : $"Downloading mpv runtime... {FormatBytes(progress.BytesTransferred)}.",
            "extracting" => progress.ProgressRatio is double ratio
                ? $"Extracting mpv runtime... {ratio:P0} ({progress.ItemsCompleted ?? 0} / {progress.TotalItems ?? 0})."
                : "Extracting mpv runtime...",
            "ready" => "mpv runtime is ready.",
            _ => "Preparing mpv runtime..."
        };

        PlaybackStatusText.Text = message;
        SetOverlayStatus(message);
    }

    private void HandleFfmpegRuntimeInstallProgress(RuntimeInstallProgress progress)
    {
        var message = progress.Stage switch
        {
            "downloading" => progress.ProgressRatio is double ratio
                ? $"Downloading ffmpeg runtime... {ratio:P0} ({FormatBytes(progress.BytesTransferred)} / {FormatBytes(progress.TotalBytes ?? 0)})."
                : $"Downloading ffmpeg runtime... {FormatBytes(progress.BytesTransferred)}.",
            "extracting" => progress.ProgressRatio is double ratio
                ? $"Extracting ffmpeg runtime... {ratio:P0} ({progress.ItemsCompleted ?? 0} / {progress.TotalItems ?? 0})."
                : "Extracting ffmpeg runtime...",
            "ready" => "ffmpeg runtime is ready.",
            _ => "Preparing ffmpeg runtime..."
        };

        Dispatcher.Invoke(() =>
        {
            PlaybackStatusText.Text = message;
            SetOverlayStatus(message);
        });
    }

    private void Player_MediaFailed(string message)
    {
        PlaybackStatusText.Text = message;
        SetOverlayStatus(message);
    }

    private void Player_TracksChanged(IReadOnlyList<MediaTrackInfo> tracks)
    {
        _currentTracks = tracks.ToList();
        RebuildAudioTrackMenu();
        RebuildEmbeddedSubtitleTrackMenu();
    }

    private static string FormatSubtitleModelTransferStatus(ModelTransferProgress progress)
    {
        var modelName = progress.ModelLabel switch
        {
            "TinyEn" => "local Tiny.en",
            "BaseEn" => "local Base.en",
            "SmallEn" => "local Small.en",
            _ => progress.ModelLabel
        };

        return progress.Stage switch
        {
            "downloading" => progress.ProgressRatio is double ratio
                ? $"Downloading {modelName} for subtitles... {ratio:P0} ({FormatBytes(progress.BytesTransferred)} / {FormatBytes(progress.TotalBytes ?? 0)})."
                : $"Downloading {modelName} for subtitles... {FormatBytes(progress.BytesTransferred)}.",
            "loading" => $"Loading {modelName} for subtitles...",
            "ready" => $"{modelName} is ready. Generating captions...",
            _ => $"Preparing {modelName} for subtitles..."
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.#} {units[unitIndex]}";
    }

    private static void OpenExternalLink(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private void PlaybackSurface_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isPlaybackSurfaceHovered = true;
        UpdateTransportVisibility();
    }

    private void PlaybackSurface_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isPlaybackSurfaceHovered = false;
        UpdateTransportVisibility();
    }

    private void RefreshSubtitleOverlay()
    {
        if (SubtitleText is null || ShowSubtitlesMenuItem is null || SubtitleOverlayContainer is null)
        {
            return;
        }

        if (ShowSubtitlesMenuItem.IsChecked != true || _appSettings.SubtitleRenderMode == SubtitleRenderMode.Off)
        {
            SubtitleOverlayContainer.Visibility = Visibility.Collapsed;
            SubtitleSourceText.Visibility = Visibility.Collapsed;
            SubtitleSourceText.Text = string.Empty;
            SubtitleText.Visibility = Visibility.Collapsed;
            SubtitleText.Text = string.Empty;
            return;
        }

        var cue = _subtitleManager.HasCues ? _subtitleManager.GetCueAt(Player.Position) : null;
        var sourceText = cue?.SourceText?.Trim();
        var translatedText = cue?.DisplayText?.Trim();
        if (string.IsNullOrWhiteSpace(translatedText))
        {
            translatedText = _overlayStatusText;
        }

        if (string.IsNullOrWhiteSpace(sourceText) && string.IsNullOrWhiteSpace(translatedText))
        {
            SubtitleOverlayContainer.Visibility = Visibility.Collapsed;
            SubtitleSourceText.Visibility = Visibility.Collapsed;
            SubtitleSourceText.Text = string.Empty;
            SubtitleText.Visibility = Visibility.Collapsed;
            SubtitleText.Text = string.Empty;
            return;
        }

        var renderMode = _appSettings.SubtitleRenderMode;
        var showSourceLine = renderMode == SubtitleRenderMode.Dual
            && !string.IsNullOrWhiteSpace(sourceText)
            && !string.Equals(sourceText, translatedText, StringComparison.Ordinal);
        var primaryText = renderMode switch
        {
            SubtitleRenderMode.SourceOnly => sourceText,
            SubtitleRenderMode.TranslationOnly => translatedText,
            SubtitleRenderMode.Dual when !string.IsNullOrWhiteSpace(translatedText) => translatedText,
            SubtitleRenderMode.Dual => sourceText,
            _ => translatedText
        };

        if (string.IsNullOrWhiteSpace(primaryText))
        {
            primaryText = sourceText;
        }

        SubtitleOverlayContainer.Visibility = Visibility.Visible;
        SubtitleSourceText.Visibility = showSourceLine ? Visibility.Visible : Visibility.Collapsed;
        SubtitleSourceText.Text = showSourceLine ? sourceText : string.Empty;
        SubtitleText.Visibility = string.IsNullOrWhiteSpace(primaryText) ? Visibility.Collapsed : Visibility.Visible;
        SubtitleText.Text = primaryText ?? string.Empty;
    }

    private void UpdateTransportVisibility()
    {
        if (TransportPanel is null)
        {
            return;
        }

        var shouldShow = Player.Source is null
            || _isPaused
            || _isSeeking
            || _isPlaybackSurfaceHovered;

        TransportPanel.Opacity = shouldShow ? 1.0 : 0.0;
        TransportPanel.IsHitTestVisible = shouldShow;
    }

    private async Task ReprocessCurrentSubtitlesForTranscriptionModelAsync(TranscriptionModelSelection selection)
    {
        if (_subtitleSource == SubtitlePipelineSource.Generated && !string.IsNullOrWhiteSpace(_currentVideoPath))
        {
            PlaybackStatusText.Text = $"Restarting transcription with {selection.DisplayName}.";
            await StartAutomaticCaptionGenerationAsync(_currentVideoPath);
            return;
        }

        PlaybackStatusText.Text = $"Selected transcription model: {selection.DisplayName}.";
    }

    private void UpdateSubtitleModelMenuChecks(string? selectedKey = null)
    {
        selectedKey ??= _selectedSubtitleModelKey;
        var items = new[]
        {
            SubtitleModelLocalTinyMenuItem,
            SubtitleModelLocalBaseMenuItem,
            SubtitleModelLocalSmallMenuItem,
            SubtitleModelCloudMiniMenuItem,
            SubtitleModelCloudGpt4oMenuItem,
            SubtitleModelCloudWhisperMenuItem
        };

        foreach (var item in items)
        {
            if (item is not null && item.Tag is string key)
            {
                item.IsChecked = string.Equals(key, selectedKey, StringComparison.Ordinal);
            }
        }
    }

    private void UpdateTranslationModelMenuChecks(string? selectedKey = null)
    {
        selectedKey ??= _selectedTranslationModelKey;
        var items = new[]
        {
            TranslationModelLocalHyMt18BMenuItem,
            TranslationModelLocalHyMt7BMenuItem,
            TranslationModelCloudOpenAiMenuItem,
            TranslationModelCloudGoogleMenuItem,
            TranslationModelCloudDeepLMenuItem,
            TranslationModelCloudMicrosoftMenuItem
        };

        foreach (var item in items)
        {
            if (item is not null && item.Tag is string key)
            {
                item.IsChecked = string.Equals(key, selectedKey, StringComparison.Ordinal);
            }
        }
    }

    private void UpdateAutoTranslateMenuChecks()
    {
        _suppressTranslationMenuEvents = true;
        try
        {
            if (AutoTranslateNonEnglishMenuItem is not null)
            {
                AutoTranslateNonEnglishMenuItem.IsChecked = _autoTranslateVideosOutsidePreferredLanguage;
            }
        }
        finally
        {
            _suppressTranslationMenuEvents = false;
        }
    }

    private void UpdateSubtitleRenderModeMenuChecks()
    {
        if (SubtitleRenderOffMenuItem is null)
        {
            return;
        }

        SubtitleRenderOffMenuItem.IsChecked = _appSettings.SubtitleRenderMode == SubtitleRenderMode.Off;
        SubtitleRenderSourceOnlyMenuItem.IsChecked = _appSettings.SubtitleRenderMode == SubtitleRenderMode.SourceOnly;
        SubtitleRenderTranslationOnlyMenuItem.IsChecked = _appSettings.SubtitleRenderMode == SubtitleRenderMode.TranslationOnly;
        SubtitleRenderDualMenuItem.IsChecked = _appSettings.SubtitleRenderMode == SubtitleRenderMode.Dual;
    }

    private void UpdateHardwareDecodingMenuChecks()
    {
        if (HardwareDecodingAutoMenuItem is null)
        {
            return;
        }

        HardwareDecodingAutoMenuItem.IsChecked = _appSettings.HardwareDecodingMode == HardwareDecodingMode.AutoSafe;
        HardwareDecodingD3D11MenuItem.IsChecked = _appSettings.HardwareDecodingMode == HardwareDecodingMode.D3D11;
        HardwareDecodingNvdecMenuItem.IsChecked = _appSettings.HardwareDecodingMode == HardwareDecodingMode.Nvdec;
        HardwareDecodingSoftwareMenuItem.IsChecked = _appSettings.HardwareDecodingMode == HardwareDecodingMode.Software;
    }

    private void UpdateAspectRatioMenuChecks()
    {
        if (AspectRatioAutoMenuItem is null)
        {
            return;
        }

        AspectRatioAutoMenuItem.IsChecked = string.Equals(_selectedAspectRatio, "auto", StringComparison.OrdinalIgnoreCase);
        AspectRatioWideMenuItem.IsChecked = string.Equals(_selectedAspectRatio, "16:9", StringComparison.OrdinalIgnoreCase);
        AspectRatioClassicMenuItem.IsChecked = string.Equals(_selectedAspectRatio, "4:3", StringComparison.OrdinalIgnoreCase);
        AspectRatioStretchMenuItem.IsChecked = string.Equals(_selectedAspectRatio, "-1", StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildAudioTrackMenu()
    {
        if (AudioTracksMenuItem is null)
        {
            return;
        }

        AudioTracksMenuItem.Items.Clear();
        var audioTracks = _currentTracks.Where(track => track.Kind == MediaTrackKind.Audio).OrderBy(track => track.Id).ToList();
        if (audioTracks.Count == 0)
        {
            AudioTracksMenuItem.Items.Add(new MenuItem
            {
                Header = "No alternate tracks",
                IsEnabled = false
            });
            return;
        }

        foreach (var track in audioTracks)
        {
            AudioTracksMenuItem.Items.Add(CreateTrackMenuItem(track, AudioTrackMenuItem_Click));
        }
    }

    private void RebuildEmbeddedSubtitleTrackMenu()
    {
        if (EmbeddedSubtitleTracksMenuItem is null)
        {
            return;
        }

        EmbeddedSubtitleTracksMenuItem.Items.Clear();
        EmbeddedSubtitleTracksMenuItem.Items.Add(new MenuItem
        {
            Header = "Off",
            IsCheckable = true,
            IsChecked = !_currentTracks.Any(track => track.Kind == MediaTrackKind.Subtitle && track.IsSelected),
            Tag = "off"
        });

        ((MenuItem)EmbeddedSubtitleTracksMenuItem.Items[0]).Click += EmbeddedSubtitleTrackMenuItem_Click;

        var subtitleTracks = _currentTracks.Where(track => track.Kind == MediaTrackKind.Subtitle).OrderBy(track => track.Id).ToList();
        if (subtitleTracks.Count == 0)
        {
            EmbeddedSubtitleTracksMenuItem.Items.Add(new Separator());
            EmbeddedSubtitleTracksMenuItem.Items.Add(new MenuItem
            {
                Header = "No embedded subtitle tracks",
                IsEnabled = false
            });
            return;
        }

        EmbeddedSubtitleTracksMenuItem.Items.Add(new Separator());
        foreach (var track in subtitleTracks)
        {
            EmbeddedSubtitleTracksMenuItem.Items.Add(CreateTrackMenuItem(track, EmbeddedSubtitleTrackMenuItem_Click));
        }
    }

    private MenuItem CreateTrackMenuItem(MediaTrackInfo track, RoutedEventHandler clickHandler)
    {
        var label = string.IsNullOrWhiteSpace(track.Title)
            ? $"{track.Language.ToUpperInvariant()} · Track {track.Id}"
            : $"{track.Title} ({track.Language.ToUpperInvariant()})";

        if (track.Kind == MediaTrackKind.Subtitle && !track.IsTextBased)
        {
            label += " · image-based";
        }

        var item = new MenuItem
        {
            Header = label,
            Tag = track.Id,
            IsCheckable = true,
            IsChecked = track.IsSelected,
            ToolTip = track.Codec
        };
        item.Click += clickHandler;
        return item;
    }

    private void AudioTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: int trackId } item)
        {
            return;
        }

        Player.SelectAudioTrack(trackId);
        PlaybackStatusText.Text = $"Selected audio track: {item.Header}.";
    }

    private async void EmbeddedSubtitleTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }

        if (item.Tag is string offValue && offValue == "off")
        {
            Player.SelectSubtitleTrack(null);
            PlaybackStatusText.Text = "Embedded subtitle track disabled.";
            return;
        }

        if (item.Tag is int trackId)
        {
            var track = _currentTracks.FirstOrDefault(candidate => candidate.Kind == MediaTrackKind.Subtitle && candidate.Id == trackId);
            if (track is null)
            {
                return;
            }

            if (track.IsTextBased)
            {
                await LoadEmbeddedSubtitleTrackAsync(track);
                return;
            }

            Player.SelectSubtitleTrack(trackId);
            PlaybackStatusText.Text = "Selected image-based embedded subtitle track for direct playback.";
        }
    }

    private void SubtitleRenderModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string modeTag })
        {
            return;
        }

        _appSettings = _appSettings with
        {
            SubtitleRenderMode = modeTag switch
            {
                "off" => SubtitleRenderMode.Off,
                "source" => SubtitleRenderMode.SourceOnly,
                "dual" => SubtitleRenderMode.Dual,
                _ => SubtitleRenderMode.TranslationOnly
            }
        };

        AppStateStore.SaveSettings(_appSettings);
        UpdateSubtitleRenderModeMenuChecks();
        RefreshSubtitleOverlay();
    }

    private void HardwareDecodingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string hwTag })
        {
            return;
        }

        _appSettings = _appSettings with
        {
            HardwareDecodingMode = hwTag switch
            {
                "d3d11" => HardwareDecodingMode.D3D11,
                "nvdec" => HardwareDecodingMode.Nvdec,
                "software" => HardwareDecodingMode.Software,
                _ => HardwareDecodingMode.AutoSafe
            }
        };

        Player.SetHardwareDecodingMode(_appSettings.HardwareDecodingMode);
        AppStateStore.SaveSettings(_appSettings);
        UpdateHardwareDecodingMenuChecks();
        PlaybackStatusText.Text = $"Hardware decoding: {hwTag}.";
    }

    private void AspectRatioMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string aspectRatio })
        {
            return;
        }

        _selectedAspectRatio = aspectRatio;
        Player.SetAspectRatio(aspectRatio);
        UpdateAspectRatioMenuChecks();
        PersistPlaybackSettings();
        PlaybackStatusText.Text = $"Aspect ratio: {(aspectRatio == "-1" ? "fill" : aspectRatio)}.";
    }

    private void SubtitleDelayBackMenuItem_Click(object sender, RoutedEventArgs e) => AdjustSubtitleDelay(-0.05);
    private void SubtitleDelayForwardMenuItem_Click(object sender, RoutedEventArgs e) => AdjustSubtitleDelay(0.05);
    private void ResetSubtitleDelayMenuItem_Click(object sender, RoutedEventArgs e) => ResetSubtitleDelay();
    private void AudioDelayBackMenuItem_Click(object sender, RoutedEventArgs e) => AdjustAudioDelay(-0.05);
    private void AudioDelayForwardMenuItem_Click(object sender, RoutedEventArgs e) => AdjustAudioDelay(0.05);
    private void ResetAudioDelayMenuItem_Click(object sender, RoutedEventArgs e) => ResetAudioDelay();
    private void BorderlessWindowMenuItem_Click(object sender, RoutedEventArgs e) => ApplyWindowMode(PlaybackWindowMode.Borderless);
    private void PictureInPictureMenuItem_Click(object sender, RoutedEventArgs e) => ApplyWindowMode(PlaybackWindowMode.PictureInPicture);
    private void FullscreenMenuItem_Click(object sender, RoutedEventArgs e) => FullscreenButton_Click(sender, e);
    private void IncreaseSubtitleFontMenuItem_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(style => style with
    {
        SourceFontSize = Math.Min(style.SourceFontSize + 2, 44),
        TranslationFontSize = Math.Min(style.TranslationFontSize + 2, 48)
    });
    private void DecreaseSubtitleFontMenuItem_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(style => style with
    {
        SourceFontSize = Math.Max(style.SourceFontSize - 2, 18),
        TranslationFontSize = Math.Max(style.TranslationFontSize - 2, 20)
    });
    private void IncreaseSubtitleBackgroundMenuItem_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(style => style with
    {
        BackgroundOpacity = Math.Min(style.BackgroundOpacity + 0.08, 0.95)
    });
    private void DecreaseSubtitleBackgroundMenuItem_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(style => style with
    {
        BackgroundOpacity = Math.Max(style.BackgroundOpacity - 0.08, 0.15)
    });
    private void RaiseSubtitlesMenuItem_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(style => style with
    {
        BottomMargin = Math.Min(style.BottomMargin + 10, 80)
    });
    private void LowerSubtitlesMenuItem_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(style => style with
    {
        BottomMargin = Math.Max(style.BottomMargin - 10, 0)
    });
    private void ShowBrowserPanelMenuItem_Click(object sender, RoutedEventArgs e) => SetBrowserPanelVisibility(ShowBrowserPanelMenuItem.IsChecked == true);
    private void ShowPlaylistPanelMenuItem_Click(object sender, RoutedEventArgs e) => SetPlaylistPanelVisibility(ShowPlaylistPanelMenuItem.IsChecked == true);

    private void TranslationColorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string hex })
        {
            return;
        }

        UpdateSubtitleStyle(style => style with { TranslationForegroundHex = hex });
    }

    private void EditShortcutsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var editor = new ShortcutEditorWindow(_appSettings.ShortcutProfile)
        {
            Owner = this
        };

        if (editor.ShowDialog() != true || editor.ResultProfile is null)
        {
            return;
        }

        _appSettings = _appSettings with { ShortcutProfile = editor.ResultProfile };
        AppStateStore.SaveSettings(_appSettings);
        PlaybackStatusText.Text = "Shortcut bindings updated.";
    }

    private void UpdateSubtitleStyle(Func<SubtitleStyleSettings, SubtitleStyleSettings> updater)
    {
        _appSettings = _appSettings with
        {
            SubtitleStyle = updater(_appSettings.SubtitleStyle)
        };
        AppStateStore.SaveSettings(_appSettings);
        ApplySubtitleStyleSettings();
        RefreshSubtitleOverlay();
    }

    private void SetBrowserPanelVisibility(bool isVisible)
    {
        _appSettings = _appSettings with { ShowBrowserPanel = isVisible };
        ApplySidePanelVisibility();
        UpdatePanelVisibilityMenuChecks();
        AppStateStore.SaveSettings(_appSettings);
    }

    private void SetPlaylistPanelVisibility(bool isVisible)
    {
        _appSettings = _appSettings with { ShowPlaylistPanel = isVisible };
        ApplySidePanelVisibility();
        UpdatePanelVisibilityMenuChecks();
        AppStateStore.SaveSettings(_appSettings);
    }

    private TranscriptionModelSelection GetSelectedTranscriptionModel()
    {
        return GetTranscriptionModel(_selectedSubtitleModelKey);
    }

    private static string GetPersistedSubtitleModelKey()
    {
        var savedKey = SecureSettingsStore.GetSubtitleModelKey();
        return savedKey is "local:tiny" or "local:base" or "local:small" or "cloud:gpt-4o-mini-transcribe" or "cloud:gpt-4o-transcribe" or "cloud:whisper-1"
            ? savedKey
            : DefaultSubtitleModelKey;
    }

    private static TranscriptionModelSelection GetTranscriptionModel(string modelKey)
    {
        return modelKey switch
        {
            "local:tiny" => new TranscriptionModelSelection(modelKey, "local Tiny.en", TranscriptionProvider.Local, Whisper.net.Ggml.GgmlType.TinyEn, null),
            "local:base" => new TranscriptionModelSelection(modelKey, "local Base.en", TranscriptionProvider.Local, Whisper.net.Ggml.GgmlType.BaseEn, null),
            "local:small" => new TranscriptionModelSelection(modelKey, "local Small.en", TranscriptionProvider.Local, Whisper.net.Ggml.GgmlType.SmallEn, null),
            "cloud:gpt-4o-mini-transcribe" => new TranscriptionModelSelection(modelKey, "cloud GPT-4o Mini Transcribe", TranscriptionProvider.Cloud, null, "gpt-4o-mini-transcribe"),
            "cloud:gpt-4o-transcribe" => new TranscriptionModelSelection(modelKey, "cloud GPT-4o Transcribe", TranscriptionProvider.Cloud, null, "gpt-4o-transcribe"),
            "cloud:whisper-1" => new TranscriptionModelSelection(modelKey, "cloud Whisper-1", TranscriptionProvider.Cloud, null, "whisper-1"),
            _ => new TranscriptionModelSelection(DefaultSubtitleModelKey, "local Base.en", TranscriptionProvider.Local, Whisper.net.Ggml.GgmlType.BaseEn, null)
        };
    }

    private TranslationModelSelection GetSelectedTranslationModel()
    {
        return GetTranslationModel(_selectedTranslationModelKey);
    }

    private static string? GetPersistedTranslationModelKey()
    {
        var savedKey = SecureSettingsStore.GetTranslationModelKey();
        if (savedKey is not "local:hymt-1.8b" and not "local:hymt-7b" and not "cloud:gpt-5-mini" and not "cloud:google-translate" and not "cloud:deepl" and not "cloud:microsoft-translator")
        {
            return null;
        }

        var selection = GetTranslationModel(savedKey);
        if (selection.Provider is TranslationProvider.OpenAi or TranslationProvider.Google or TranslationProvider.DeepL or TranslationProvider.MicrosoftTranslator)
        {
            return HasConfiguredTranslationProvider(selection.Provider) ? savedKey : null;
        }

        if (selection.Provider is TranslationProvider.LocalHyMt15_1_8B or TranslationProvider.LocalHyMt15_7B)
        {
            return TryResolveLlamaCppServerPath() is not null ? savedKey : null;
        }

        return null;
    }

    private static TranslationModelSelection GetTranslationModel(string? modelKey)
    {
        return modelKey switch
        {
            "local:hymt-1.8b" => new TranslationModelSelection(modelKey, "local HY-MT1.5 1.8B", TranslationProvider.LocalHyMt15_1_8B, null),
            "local:hymt-7b" => new TranslationModelSelection(modelKey, "local HY-MT1.5 7B", TranslationProvider.LocalHyMt15_7B, null),
            "cloud:gpt-5-mini" => new TranslationModelSelection(modelKey, "cloud OpenAI GPT-5 mini", TranslationProvider.OpenAi, "gpt-5-mini"),
            "cloud:google-translate" => new TranslationModelSelection(modelKey, "cloud Google Translate", TranslationProvider.Google, null),
            "cloud:deepl" => new TranslationModelSelection(modelKey, "cloud DeepL API", TranslationProvider.DeepL, null),
            "cloud:microsoft-translator" => new TranslationModelSelection(modelKey, "cloud Microsoft Translator", TranslationProvider.MicrosoftTranslator, null),
            _ => new TranslationModelSelection(string.Empty, "No translation model", TranslationProvider.None, null)
        };
    }

    private static bool HasConfiguredTranslationProvider(TranslationProvider provider)
    {
        return provider switch
        {
            TranslationProvider.None => false,
            TranslationProvider.LocalHyMt15_1_8B => TryResolveLlamaCppServerPath() is not null,
            TranslationProvider.LocalHyMt15_7B => TryResolveLlamaCppServerPath() is not null,
            TranslationProvider.OpenAi => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? SecureSettingsStore.GetOpenAiApiKey()),
            TranslationProvider.Google => !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("GOOGLE_TRANSLATE_API_KEY")
                ?? Environment.GetEnvironmentVariable("GOOGLE_CLOUD_TRANSLATE_API_KEY")
                ?? SecureSettingsStore.GetGoogleTranslateApiKey()),
            TranslationProvider.DeepL => !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("DEEPL_API_KEY")
                ?? SecureSettingsStore.GetDeepLApiKey()),
            TranslationProvider.MicrosoftTranslator => !string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("MICROSOFT_TRANSLATOR_API_KEY")
                    ?? Environment.GetEnvironmentVariable("AZURE_TRANSLATOR_KEY")
                    ?? SecureSettingsStore.GetMicrosoftTranslatorApiKey())
                && !string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("MICROSOFT_TRANSLATOR_REGION")
                    ?? Environment.GetEnvironmentVariable("AZURE_TRANSLATOR_REGION")
                    ?? SecureSettingsStore.GetMicrosoftTranslatorRegion()),
            _ => false
        };
    }

    private void InitializeTranslationPreferencesForNewVideo()
    {
        _currentVideoTranslationPreferenceLocked = false;
        SetTranslationEnabledForCurrentVideo(false, reprocessExistingCues: false);
    }

    private void ApplyAutomaticTranslationPreferenceIfNeeded()
    {
        if (_currentVideoTranslationPreferenceLocked)
        {
            return;
        }

        if (!_autoTranslateVideosOutsidePreferredLanguage)
        {
            SetTranslationEnabledForCurrentVideo(false, reprocessExistingCues: false);
            return;
        }

        if (!HasSelectedTranslationModel())
        {
            SetTranslationEnabledForCurrentVideo(false, reprocessExistingCues: false);
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentSourceLanguage) || string.Equals(_currentSourceLanguage, "und", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var shouldTranslate = !IsLanguageCode(_currentSourceLanguage, _autoTranslatePreferredSourceLanguage);
        SetTranslationEnabledForCurrentVideo(shouldTranslate, reprocessExistingCues: false);
    }

    private void SetTranslationEnabledForCurrentVideo(bool enabled, bool reprocessExistingCues)
    {
        _isTranslationEnabledForCurrentVideo = enabled;
        _suppressTranslationMenuEvents = true;
        try
        {
            if (EnableTranslationForCurrentVideoMenuItem is not null)
            {
                EnableTranslationForCurrentVideoMenuItem.IsChecked = enabled;
            }
        }
        finally
        {
            _suppressTranslationMenuEvents = false;
        }

        if (reprocessExistingCues)
        {
            _ = ReprocessCurrentSubtitlesForTranslationSettingsAsync();
        }
    }

    private async Task ReprocessCurrentSubtitlesForTranslationSettingsAsync()
    {
        if (_subtitleSource == SubtitlePipelineSource.Generated && !string.IsNullOrWhiteSpace(_currentVideoPath))
        {
            PlaybackStatusText.Text = _isTranslationEnabledForCurrentVideo
                ? "Updating generated subtitle translation."
                : "Showing source-language subtitles for the current video.";
            ResetCurrentTranslations();
            RefreshSubtitleOverlay();
            return;
        }

        if (_subtitleManager.HasCues)
        {
            ResetCurrentTranslations();
            PlaybackStatusText.Text = _isTranslationEnabledForCurrentVideo
                ? "Updating subtitle translation for the current video."
                : "Translation disabled for the current video.";
            var cts = new CancellationTokenSource();
            _translationCts?.Cancel();
            _translationCts?.Dispose();
            _translationCts = cts;
            _ = TranslateAllCuesAsync(cts.Token);
            return;
        }

        RefreshSubtitleOverlay();
        await Task.CompletedTask;
    }
}

internal enum TranscriptionProvider
{
    Local,
    Cloud
}

internal enum TranslationProvider
{
    None,
    LocalHyMt15_1_8B,
    LocalHyMt15_7B,
    OpenAi,
    Google,
    DeepL,
    MicrosoftTranslator
}

internal sealed record TranscriptionModelSelection(
    string Key,
    string DisplayName,
    TranscriptionProvider Provider,
    Whisper.net.Ggml.GgmlType? LocalModelType,
    string? CloudModel);

internal sealed record TranslationModelSelection(
    string Key,
    string DisplayName,
    TranslationProvider Provider,
    string? CloudModel);

internal sealed class ApiKeyPromptWindow : Window
{
    private readonly PasswordBox _apiKeyBox;

    public ApiKeyPromptWindow(string title, string message, string submitButtonText)
    {
        Title = title;
        Width = 440;
        Height = 190;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.White;

        var root = new Grid
        {
            Margin = new Thickness(16)
        };

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(text, 0);

        _apiKeyBox = new PasswordBox
        {
            Margin = new Thickness(0, 0, 0, 14)
        };
        Grid.SetRow(_apiKeyBox, 1);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };

        var okButton = new Button
        {
            Content = submitButtonText,
            MinWidth = 84,
            IsDefault = true
        };
        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_apiKeyBox.Password))
            {
                MessageBox.Show(this, "Enter an API key or cancel.", "API Key Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        };

        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);
        Grid.SetRow(buttons, 2);

        root.Children.Add(text);
        root.Children.Add(_apiKeyBox);
        root.Children.Add(buttons);

        Content = root;
    }

    public string ApiKey => _apiKeyBox.Password;
}

internal sealed class ApiKeyWithRegionPromptWindow : Window
{
    private readonly PasswordBox _apiKeyBox;
    private readonly TextBox _regionBox;

    public ApiKeyWithRegionPromptWindow(string title, string message, string submitButtonText)
    {
        Title = title;
        Width = 460;
        Height = 250;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.White;

        var root = new Grid
        {
            Margin = new Thickness(16)
        };

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(text, 0);

        var apiKeyLabel = new TextBlock
        {
            Text = "API Key",
            Margin = new Thickness(0, 0, 0, 6),
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(apiKeyLabel, 1);

        _apiKeyBox = new PasswordBox
        {
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(_apiKeyBox, 2);

        var regionLabel = new TextBlock
        {
            Text = "Azure Region",
            Margin = new Thickness(0, 0, 0, 6),
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(regionLabel, 3);

        _regionBox = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 14)
        };
        Grid.SetRow(_regionBox, 4);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };

        var okButton = new Button
        {
            Content = submitButtonText,
            MinWidth = 110,
            IsDefault = true
        };
        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_apiKeyBox.Password) || string.IsNullOrWhiteSpace(_regionBox.Text))
            {
                MessageBox.Show(this, "Enter both the API key and Azure region, or cancel.", "Credentials Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        };

        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);
        Grid.SetRow(buttons, 5);

        root.Children.Add(text);
        root.Children.Add(apiKeyLabel);
        root.Children.Add(_apiKeyBox);
        root.Children.Add(regionLabel);
        root.Children.Add(_regionBox);
        root.Children.Add(buttons);

        Content = root;
    }

    public string ApiKey => _apiKeyBox.Password;
    public string Region => _regionBox.Text;
}

internal enum SubtitlePipelineSource
{
    None,
    Sidecar,
    Manual,
    EmbeddedTrack,
    Generated
}

