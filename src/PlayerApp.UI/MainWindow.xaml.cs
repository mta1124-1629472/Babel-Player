using Microsoft.Win32;
using PlayerApp.Core;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PlayerApp.UI;

public partial class MainWindow : Window
{
    private const string DefaultLocalSpeechCulture = "en-US";
    private const string DefaultSubtitleModelKey = "local:base";
    private readonly SubtitleManager _subtitleManager = new();
    private readonly MtService _translator = new();
    private readonly DispatcherTimer _subtitleTimer = new();
    private readonly object _translationSync = new();
    private readonly HashSet<SubtitleCue> _inFlightCueTranslations = [];

    private CancellationTokenSource? _translationCts;
    private CancellationTokenSource? _captionGenerationCts;
    private string? _sessionOpenAiApiKey;
    private string? _currentVideoPath;
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
    private string _translationTargetLanguage = "en";
    private string _autoTranslatePreferredSourceLanguage = "en";
    private int _activeCaptionGenerationId;
    private string _currentSourceLanguage = "und";
    private SubtitlePipelineSource _subtitleSource = SubtitlePipelineSource.None;

    public MainWindow()
    {
        InitializeComponent();

        _selectedSubtitleModelKey = GetPersistedSubtitleModelKey();
        _selectedTranslationModelKey = GetPersistedTranslationModelKey();
        _autoTranslateVideosOutsidePreferredLanguage = SecureSettingsStore.GetAutoTranslateEnabled();
        UpdateSubtitleModelMenuChecks();
        UpdateTranslationModelMenuChecks();
        UpdateAutoTranslateMenuChecks();

        HardwareStatusText.Text = HardwareDetector.GetSummary();
        PlaybackStatusText.Text = "Ready";
        _sessionOpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? SecureSettingsStore.GetOpenAiApiKey();
        _translator.OnLocalRuntimeStatus += HandleLocalTranslationRuntimeStatus;
        ConfigureTranslator();
        Player.Volume = 0.8;

        _subtitleTimer.Interval = TimeSpan.FromMilliseconds(120);
        _subtitleTimer.Tick += SubtitleTimer_Tick;

        Player.MediaOpened += Player_MediaOpened;
        Player.MediaEnded += Player_MediaEnded;

        Closed += MainWindow_Closed;
        Loaded += (_, _) => UpdateTransportVisibility();
    }

    private async void OpenVideoButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm|All files|*.*",
            Title = "Choose a local video"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var path = dialog.FileName;
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
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.CanResize;
            FullscreenButton.Content = "⛶";
            return;
        }

        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        ResizeMode = ResizeMode.NoResize;
        FullscreenButton.Content = "🗗";
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Player is null)
        {
            return;
        }

        Player.Volume = Math.Clamp(VolumeSlider.Value, 0, 1);
    }

    private async void OpenSubtitlesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "SubRip subtitles|*.srt|All files|*.*",
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
        InitializeTranslationPreferencesForNewVideo();
        var cues = SubtitleFileService.ParseSrt(path);
        _subtitleManager.LoadCues(cues);
        _subtitleSource = autoLoaded ? SubtitlePipelineSource.Sidecar : SubtitlePipelineSource.Manual;
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
            PlaybackStatusText.Text = $"No playable subtitle cues in {Path.GetFileName(path)}.";
            SetOverlayStatus("Loaded subtitle file contains no playable cues.");
            return;
        }

        _currentSourceLanguage = ApplySourceLanguageToCues(_subtitleManager.Cues);
        ApplyAutomaticTranslationPreferenceIfNeeded();

        PlaybackStatusText.Text = autoLoaded
            ? $"Loaded sidecar subtitles: {Path.GetFileName(path)} ({_subtitleManager.CueCount} cues)."
            : $"Loaded subtitles: {Path.GetFileName(path)} ({_subtitleManager.CueCount} cues).";

        SetOverlayStatus(_isTranslationEnabledForCurrentVideo
            ? "Preparing translated subtitles..."
            : "Preparing source-language subtitles...");

        var cts = new CancellationTokenSource();
        _translationCts = cts;
        _ = TranslateAllCuesAsync(cts.Token);
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

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        UpdateTransportDuration();
        UpdateTransportPosition();
        UpdateTransportVisibility();
        _subtitleTimer.Start();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        _subtitleTimer.Stop();
        _isPaused = true;
        PlayPauseButton.Content = "▶";
        if (Player.NaturalDuration.HasTimeSpan)
        {
            Player.Position = Player.NaturalDuration.TimeSpan;
        }

        UpdateTransportPosition();
        UpdateTransportVisibility();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        CancelBackgroundWork();
        _subtitleTimer.Stop();
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

        if (ShowSubtitlesMenuItem.IsChecked != true)
        {
            SubtitleOverlayContainer.Visibility = Visibility.Collapsed;
            SubtitleText.Visibility = Visibility.Collapsed;
            SubtitleText.Text = string.Empty;
            return;
        }

        var cue = _subtitleManager.HasCues ? _subtitleManager.GetCueAt(Player.Position) : null;
        var text = cue?.DisplayText;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = _overlayStatusText;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            SubtitleOverlayContainer.Visibility = Visibility.Collapsed;
            SubtitleText.Visibility = Visibility.Collapsed;
            SubtitleText.Text = string.Empty;
            return;
        }

        SubtitleOverlayContainer.Visibility = Visibility.Visible;
        SubtitleText.Visibility = Visibility.Visible;
        SubtitleText.Text = text;
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
    Generated
}

