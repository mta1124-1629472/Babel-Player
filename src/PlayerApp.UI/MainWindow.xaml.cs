using Microsoft.Win32;
using PlayerApp.Core;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace PlayerApp.UI;

public partial class MainWindow : Window
{
    private readonly SubtitleManager _subtitleManager = new();
    private readonly MtService _translator = new();
    private readonly DispatcherTimer _subtitleTimer = new();
    private readonly object _translationSync = new();

    private CancellationTokenSource? _translationCts;
    private CancellationTokenSource? _captionGenerationCts;
    private bool _isPaused;
    private int _activeCaptionGenerationId;
    private string _loadedTranslationLanguage = "en";

    public MainWindow()
    {
        InitializeComponent();

        HardwareStatusText.Text = HardwareDetector.GetSummary();
        PlaybackStatusText.Text = "Ready";
        UseCloudTranscriptionCheckBox.IsChecked = false;

        _subtitleTimer.Interval = TimeSpan.FromMilliseconds(120);
        _subtitleTimer.Tick += SubtitleTimer_Tick;

        Player.MediaOpened += Player_MediaOpened;
        Player.MediaEnded += Player_MediaEnded;

        Closed += MainWindow_Closed;
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

        Player.Source = new Uri(path);
        Player.Play();

        _isPaused = false;
        PlayPauseButton.Content = "Pause";
        PlaybackStatusText.Text = $"Playing: {Path.GetFileName(path)}";

        var hasSubtitles = await TryLoadSidecarSubtitlesAsync(path);
        if (!hasSubtitles)
        {
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
            PlayPauseButton.Content = "Pause";
        }
        else
        {
            Player.Pause();
            _isPaused = true;
            PlayPauseButton.Content = "Play";
        }
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
            SubtitleText.Visibility = Visibility.Visible;
            SubtitleText.Text = "No sidecar subtitles found. Generating captions from the video audio.";
            return false;
        }

        await LoadSubtitlesFromPathAsync(sidecarPath, autoLoaded: true);
        return true;
    }

    private async Task LoadSubtitlesFromPathAsync(string path, bool autoLoaded)
    {
        var cues = SubtitleFileService.ParseSrt(path);
        _subtitleManager.LoadCues(cues);

        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = null;

        if (!_subtitleManager.HasCues)
        {
            PlaybackStatusText.Text = $"No playable subtitle cues in {Path.GetFileName(path)}.";
            SubtitleText.Visibility = Visibility.Visible;
            SubtitleText.Text = "Loaded subtitle file contains no playable cues.";
            return;
        }

        _loadedTranslationLanguage = "en";
        var sample = string.Join(" ", _subtitleManager.Cues.Take(3).Select(c => c.Text));
        EnsureTranslationModelLoaded(sample);

        PlaybackStatusText.Text = autoLoaded
            ? $"Loaded sidecar subtitles: {Path.GetFileName(path)} ({_subtitleManager.CueCount} cues)."
            : $"Loaded subtitles: {Path.GetFileName(path)} ({_subtitleManager.CueCount} cues).";

        SubtitleText.Visibility = Visibility.Visible;
        SubtitleText.Text = "Preparing translated subtitles...";

        var cts = new CancellationTokenSource();
        _translationCts = cts;
        _ = TranslateAllCuesAsync(cts.Token);
    }

    private async Task StartAutomaticCaptionGenerationAsync(string videoPath)
    {
        CancelCaptionGeneration();
        _subtitleManager.Clear();
        _loadedTranslationLanguage = "en";

        var generationId = Interlocked.Increment(ref _activeCaptionGenerationId);
        var cts = new CancellationTokenSource();
        _captionGenerationCts = cts;

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var useCloud = UseCloudTranscriptionCheckBox.IsChecked == true;
        var mode = useCloud && !string.IsNullOrWhiteSpace(apiKey)
            ? CaptionTranscriptionMode.Cloud
            : CaptionTranscriptionMode.Local;

        var statusText = mode == CaptionTranscriptionMode.Cloud
            ? "Generating captions with cloud transcription, with local fallback if needed."
            : "Generating captions locally from the video audio.";

        if (useCloud && string.IsNullOrWhiteSpace(apiKey))
        {
            statusText = "OPENAI_API_KEY not set. Falling back to local transcription.";
        }

        PlaybackStatusText.Text = statusText;
        SubtitleText.Visibility = Visibility.Visible;
        SubtitleText.Text = "Listening to the video audio and building translated captions...";

        var asrService = new AsrService();
        asrService.OnFinal += chunk => HandleRecognizedChunk(chunk, generationId);

        try
        {
            var cues = await asrService.TranscribeVideoAsync(
                videoPath,
                new CaptionGenerationOptions
                {
                    Mode = mode,
                    OpenAiApiKey = apiKey
                },
                cts.Token);

            if (generationId != _activeCaptionGenerationId || cts.IsCancellationRequested)
            {
                return;
            }

            PlaybackStatusText.Text = cues.Count > 0
                ? $"Generated {cues.Count} caption cues automatically."
                : "No speech could be recognized from the video audio.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generationId != _activeCaptionGenerationId)
            {
                return;
            }

            PlaybackStatusText.Text = $"Automatic caption generation failed: {ex.Message}";
            SubtitleText.Visibility = Visibility.Visible;
            SubtitleText.Text = "Automatic caption generation failed. You can still load a manual .srt file.";
        }
    }

    private async Task TranslateAllCuesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() =>
            {
                foreach (var cue in _subtitleManager.Cues)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(cue.TranslatedText))
                    {
                        continue;
                    }

                    EnsureTranslationModelLoaded(cue.Text);
                    lock (_translationSync)
                    {
                        _subtitleManager.CommitTranslation(cue, _translator.Translate(cue.Text));
                    }
                }
            }, cancellationToken);

            if (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    PlaybackStatusText.Text = $"Translated {_subtitleManager.CueCount} subtitle cues.";
                });
            }
        }
        catch (OperationCanceledException)
        {
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
            Text = chunk.Text.Trim()
        };

        EnsureTranslationModelLoaded(cue.Text);

        lock (_translationSync)
        {
            _subtitleManager.AddCue(cue);
            _subtitleManager.CommitTranslation(cue, _translator.Translate(cue.Text));
        }

        Dispatcher.Invoke(() =>
        {
            if (Player.Position >= cue.Start && Player.Position <= cue.End)
            {
                SubtitleText.Visibility = Visibility.Visible;
                SubtitleText.Text = cue.TranslatedText ?? cue.Text;
            }
        });
    }

    private void EnsureTranslationModelLoaded(string text)
    {
        var language = LanguageDetector.Detect(text);
        if (string.Equals(language, _loadedTranslationLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var modelPath = ModelManager.EnsureModelForLanguageAsync(language).GetAwaiter().GetResult();
        lock (_translationSync)
        {
            _translator.LoadModel(modelPath);
            _loadedTranslationLanguage = language;
        }
    }

    private void SubtitleTimer_Tick(object? sender, EventArgs e)
    {
        if (!_subtitleManager.HasCues)
        {
            return;
        }

        var cue = _subtitleManager.GetCueAt(Player.Position);
        if (cue is null)
        {
            SubtitleText.Visibility = Visibility.Collapsed;
            SubtitleText.Text = string.Empty;
            return;
        }

        if (string.IsNullOrWhiteSpace(cue.TranslatedText))
        {
            EnsureTranslationModelLoaded(cue.Text);
            lock (_translationSync)
            {
                _subtitleManager.CommitTranslation(cue, _translator.Translate(cue.Text));
            }
        }

        SubtitleText.Visibility = Visibility.Visible;
        SubtitleText.Text = cue.TranslatedText ?? cue.Text;
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _subtitleTimer.Start();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        _subtitleTimer.Stop();
        _isPaused = true;
        PlayPauseButton.Content = "Play";
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
    }

    private void CancelCaptionGeneration()
    {
        Interlocked.Increment(ref _activeCaptionGenerationId);
        _captionGenerationCts?.Cancel();
        _captionGenerationCts?.Dispose();
        _captionGenerationCts = null;
    }
}
