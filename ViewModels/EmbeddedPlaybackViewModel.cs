using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;

namespace Babel.Player.ViewModels;

public partial class EmbeddedPlaybackViewModel : ViewModelBase
{
    private readonly SessionWorkflowCoordinator _coordinator;
    private readonly ApiKeyStore? _apiKeyStore;
    private string? _lastKnownSourceMediaPath;
    private bool _isUpdatingPositionFromTimer;
    private bool _isUpdatingActiveSegment;
    private WorkflowSegmentState? _lastDubbedSegment;

    [ObservableProperty]
    private ObservableCollection<WorkflowSegmentState> _segments = [];

    [ObservableProperty]
    private WorkflowSegmentState? _selectedSegment;

    [ObservableProperty]
    private bool _hasSegments;

    [ObservableProperty]
    private string _statusText = "No segments loaded.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isSourceMediaLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseSourceLabel))]
    private bool _isSourcePaused = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourcePositionFormatted))]
    private double _sourcePositionMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceDurationFormatted))]
    private double _sourceDurationMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeIconLabel))]
    private double _sourceVolume = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeIconLabel))]
    private bool _isMuted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPullTabVisible))]
    private bool _isFullscreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SegmentPaneToggleLabel))]
    private bool _isSegmentPaneVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPullTabVisible))]
    private bool _isControlsVisible = true;

    [ObservableProperty]
    private string _selectedPlaybackRate = "1x";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DubModeLabel))]
    private bool _isDubModeOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleToggleLabel))]
    private bool _isSubtitleModeOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeechRateLabel))]
    private double _speechRate = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioDuckingLabel))]
    private double _audioDuckingDb = -15.0;

    // ── Provider / model selection (backed by AppSettings) ────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableTranscriptionModels))]
    [NotifyPropertyChangedFor(nameof(SelectedTranscriptionModel))]
    [NotifyPropertyChangedFor(nameof(TranscriptionKeyStatus))]
    private string _transcriptionProvider = "faster-whisper";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTranscriptionModel))]
    [NotifyPropertyChangedFor(nameof(TranscriptionKeyStatus))]
    private string _transcriptionModel = "base";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableTranslationModels))]
    [NotifyPropertyChangedFor(nameof(SelectedTranslationModel))]
    [NotifyPropertyChangedFor(nameof(TranslationKeyStatus))]
    private string _translationProvider = "google-translate-free";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTranslationModel))]
    [NotifyPropertyChangedFor(nameof(TranslationKeyStatus))]
    private string _translationModel = "default";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableTtsOptions))]
    [NotifyPropertyChangedFor(nameof(SelectedTtsOption))]
    [NotifyPropertyChangedFor(nameof(TtsKeyStatus))]
    private string _ttsProvider = "edge-tts";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTtsOption))]
    [NotifyPropertyChangedFor(nameof(TtsKeyStatus))]
    private string _ttsModelOrVoice = "en-US-AriaNeural";

    private double _preMuteVolume = 1.0;
    private double _preDuckTransportVolume = 1.0;
    private bool _isDucked;
    private bool _preFullscreenSegmentPaneVisible = true;
    private bool _preSubtitlePaneState = true;
    private string? _activeSrtPath;

    private readonly DispatcherTimer _controlsHideTimer;
    private const int ControlsHideDelayMs = 3000;

    public EmbeddedPlaybackViewModel(SessionWorkflowCoordinator coordinator, ApiKeyStore? apiKeyStore = null)
    {
        _coordinator = coordinator;
        _apiKeyStore = apiKeyStore;
        _lastKnownSourceMediaPath = coordinator.CurrentSession.SourceMediaPath;
        _isSourceMediaLoaded = !string.IsNullOrEmpty(coordinator.CurrentSession.IngestedMediaPath);

        // Sync provider/model fields from persisted settings (no side-effects — set backing fields directly)
        _transcriptionProvider = coordinator.CurrentSettings.TranscriptionProvider;
        _transcriptionModel    = coordinator.CurrentSettings.TranscriptionModel;
        _translationProvider   = coordinator.CurrentSettings.TranslationProvider;
        _translationModel      = coordinator.CurrentSettings.TranslationModel;
        _ttsProvider           = coordinator.CurrentSettings.TtsProvider;
        _ttsModelOrVoice       = coordinator.CurrentSettings.TtsVoice;

        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += OnPositionTimerTick;
        timer.Start();

        _controlsHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ControlsHideDelayMs) };
        _controlsHideTimer.Tick += OnControlsHideTimerTick;
    }

    public SessionWorkflowCoordinator Coordinator => _coordinator;

    public PlaybackState PlaybackState => _coordinator.PlaybackState;

    public string? ActiveTtsSegmentId => _coordinator.ActiveTtsSegmentId;

    public static IReadOnlyList<string> PlaybackRateOptions { get; } =
        ["0.25x", "0.5x", "0.75x", "1x", "1.25x", "1.5x", "2x"];

    public string SegmentPaneToggleLabel => IsSegmentPaneVisible ? "\u25C4" : "\u25BA";

    // Visible always in windowed mode; follows controls auto-hide in fullscreen
    public bool IsPullTabVisible => !IsFullscreen || IsControlsVisible;

    public string PlayPauseSourceLabel => IsSourcePaused ? "\u25B6\uFE0E" : "\u23F8\uFE0E";

    public string VolumeIconLabel => IsMuted || SourceVolume == 0
        ? "\U0001F507"
        : SourceVolume < 0.10
            ? "\U0001F508"
            : SourceVolume < 0.51
                ? "\U0001F509"
                : "\U0001F50A";

    public string DubModeLabel => IsDubModeOn ? "🎙 Dub: On" : "🎙 Dub: Off";

    public string SubtitleToggleLabel => IsSubtitleModeOn ? "CC ✓" : "CC";

    public string SpeechRateLabel => $"{SpeechRate:F1}x";
    public string AudioDuckingLabel => $"{AudioDuckingDb:F1} dB";
    public bool HasDiagnosticsWarning => !_coordinator.BootstrapDiagnostics.AllDependenciesAvailable;
    public string DiagnosticsWarningText => _coordinator.BootstrapDiagnostics.DiagnosticSummary;
    public string VoiceModelLabel => _coordinator.CurrentSession.TtsVoice ?? _coordinator.CurrentSettings.TtsVoice;

    // ── Provider / model option lists ──────────────────────────────────────────
    public static IReadOnlyList<string> TranscriptionProviders  => ProviderOptions.TranscriptionProviders;
    public static IReadOnlyList<string> TranslationProviders    => ProviderOptions.TranslationProviders;
    public static IReadOnlyList<string> TtsProviders            => ProviderOptions.TtsProviders;

    public IReadOnlyList<ModelOptionViewModel> AvailableTranscriptionModels =>
        [.. ProviderOptions.GetTranscriptionModels(TranscriptionProvider)
            .Select(m => new ModelOptionViewModel(m, 
                TranscriptionProvider == "faster-whisper" ? ProviderReadinessResolver.IsFasterWhisperDownloaded(m) : null))];

    public IReadOnlyList<ModelOptionViewModel> AvailableTranslationModels =>
        [.. ProviderOptions.GetTranslationModels(TranslationProvider)
            .Select(m => new ModelOptionViewModel(m, 
                TranslationProvider == "nllb-200" ? ProviderReadinessResolver.IsNllbDownloaded(m) : null))];

    public IReadOnlyList<ModelOptionViewModel> AvailableTtsOptions =>
        [.. ProviderOptions.GetTtsOptions(TtsProvider)
            .Select(m => new ModelOptionViewModel(m, 
                TtsProvider == "piper" ? ProviderReadinessResolver.IsPiperVoiceDownloaded(m, _coordinator.CurrentSettings.PiperModelDir) : null))];

    /// <summary>The currently selected transcription model as an object — used for SelectedItem binding to avoid blank box on collection swap.</summary>
    public ModelOptionViewModel? SelectedTranscriptionModel
    {
        get => AvailableTranscriptionModels.FirstOrDefault(m => m.ModelId == TranscriptionModel);
        set { if (value != null) TranscriptionModel = value.ModelId; }
    }

    /// <summary>The currently selected translation model as an object — used for SelectedItem binding.</summary>
    public ModelOptionViewModel? SelectedTranslationModel
    {
        get => AvailableTranslationModels.FirstOrDefault(m => m.ModelId == TranslationModel);
        set { if (value != null) TranslationModel = value.ModelId; }
    }

    /// <summary>The currently selected TTS voice/model as an object — used for SelectedItem binding.</summary>
    public ModelOptionViewModel? SelectedTtsOption
    {
        get => AvailableTtsOptions.FirstOrDefault(m => m.ModelId == TtsModelOrVoice);
        set { if (value != null) TtsModelOrVoice = value.ModelId; }
    }

    // ── Pipeline Progress ──────────────────────────────────────────────────────
    [ObservableProperty]
    private double _pipelineProgressPercent;

    [ObservableProperty]
    private bool _isPipelineProgressVisible;

    // ── API key status for UI lock indicators ─────────────────────────────────
    public string TranscriptionKeyStatus => ReadinessToStatusText(
        ProviderReadinessResolver.ResolveTranscription(TranscriptionProvider, TranscriptionModel, _apiKeyStore));

    public string TranslationKeyStatus => ReadinessToStatusText(
        ProviderReadinessResolver.ResolveTranslation(TranslationProvider, TranslationModel, _apiKeyStore));

    public string TtsKeyStatus => ReadinessToStatusText(
        ProviderReadinessResolver.ResolveTts(TtsProvider, TtsModelOrVoice, _coordinator.CurrentSettings.PiperModelDir, _apiKeyStore));

    private static string ReadinessToStatusText(ProviderReadiness readiness) => readiness switch
    {
        ProviderReadiness.RequiresDownload => "⬇️ Download required (will run automatically)",
        ProviderReadiness.RequiresApiKey   => "🔒 API key required — click API Keys to add",
        ProviderReadiness.Unsupported      => "⚠️ Provider not implemented yet",
        _                                  => ""
    };

    // ── Hardware display lines ─────────────────────────────────────────────────
    public string HwCpuLine  => _coordinator.HardwareSnapshot.CpuLine;
    public string HwGpuLine  => _coordinator.HardwareSnapshot.GpuLine;
    public string HwRamLine  => _coordinator.HardwareSnapshot.RamLine;
    public string HwNpuLine  => _coordinator.HardwareSnapshot.NpuLine;
    public string HwLibsLine => _coordinator.HardwareSnapshot.LibsLine;

    public string SourcePositionFormatted => FormatMs(SourcePositionMs);
    public string SourceDurationFormatted => FormatMs(SourceDurationMs);

    private static string FormatMs(double ms) =>
        ms <= 0 ? "0:00" : TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss");

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        var player = _coordinator.SourceMediaPlayer;
        if (player == null || player.Duration == 0) return;

        _isUpdatingPositionFromTimer = true;
        SourceDurationMs = player.Duration;
        SourcePositionMs = player.CurrentTime;
        _isUpdatingPositionFromTimer = false;

        UpdateActiveSegment();
        if (IsDubModeOn) UpdateDubMode();
    }

    private void UpdateActiveSegment()
    {
        var currentSeg = FindSegmentAt(SourcePositionMs / 1000.0);
        if (currentSeg?.SegmentId == SelectedSegment?.SegmentId) return;
        _isUpdatingActiveSegment = true;
        SelectedSegment = currentSeg;
        _isUpdatingActiveSegment = false;
    }

    partial void OnSourcePositionMsChanged(double value)
    {
        if (_isUpdatingPositionFromTimer) return;
        _coordinator.SourceMediaPlayer?.Seek((long)value);
        if (IsDubModeOn)
        {
            RestoreDucking();
            _coordinator.StopTtsPlayback();
            _lastDubbedSegment = null;
        }
    }

    partial void OnSelectedSegmentChanged(WorkflowSegmentState? value)
    {
        if (_isUpdatingActiveSegment || value == null || !IsSourceMediaLoaded) return;
        _ = SeekAndPlayAsync(value);
    }

    private async Task SeekAndPlayAsync(WorkflowSegmentState segment)
    {
        var player = _coordinator.SourceMediaPlayer;
        if (player == null)
        {
            // Player not yet initialised — fall back to full load path
            await PlaySourceAtSegmentAsync(segment);
            return;
        }

        player.Seek((long)(segment.StartSeconds * 1000));

        if (IsSourcePaused)
        {
            try
            {
                await Task.Run(() => player.Play());
                IsSourcePaused = false;
            }
            catch (Exception ex)
            {
                StatusText = $"Play failed: {ex.Message}";
                return;
            }
        }

        // Immediately sync dub mode to the new segment without waiting for the next timer tick
        if (IsDubModeOn)
        {
            RestoreDucking();
            _coordinator.StopTtsPlayback();
            _lastDubbedSegment = segment;
            if (segment.HasTtsAudio)
            {
                ApplyDucking();
                _ = _coordinator.PlayTtsForSegmentAsync(segment.SegmentId);
            }
        }
    }

    partial void OnSourceVolumeChanged(double value)
    {
        if (IsMuted && value > 0) IsMuted = false;
        var player = _coordinator.SourceMediaPlayer;
        if (player != null) player.Volume = value;
    }

    partial void OnSelectedPlaybackRateChanged(string value)
    {
        var num = value.TrimEnd('x');
        if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
        {
            var player = _coordinator.SourceMediaPlayer;
            if (player != null) player.PlaybackRate = rate;
        }
    }

    partial void OnSpeechRateChanged(double value)
    {
        var player = _coordinator.SourceMediaPlayer;
        if (player != null) player.PlaybackRate = value;
    }

    partial void OnTranscriptionProviderChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _coordinator.CurrentSettings.TranscriptionProvider = value;
        // Reset model to first valid option for new provider
        var models = ProviderOptions.GetTranscriptionModels(value);
        TranscriptionModel = models.Count > 0 ? models[0] : "default";
        NotifySettingsSave();
    }

    partial void OnTranscriptionModelChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _coordinator.CurrentSettings.TranscriptionModel = value;
        NotifySettingsSave();
    }

    partial void OnTranslationProviderChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _coordinator.CurrentSettings.TranslationProvider = value;
        var models = ProviderOptions.GetTranslationModels(value);
        TranslationModel = models.Count > 0 ? models[0] : "default";
        NotifySettingsSave();
    }

    partial void OnTranslationModelChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _coordinator.CurrentSettings.TranslationModel = value;
        NotifySettingsSave();
    }

    partial void OnTtsProviderChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _coordinator.CurrentSettings.TtsProvider = value;
        var options = ProviderOptions.GetTtsOptions(value);
        TtsModelOrVoice = options.Count > 0 ? options[0] : "default";
        NotifySettingsSave();
    }

    partial void OnTtsModelOrVoiceChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _coordinator.CurrentSettings.TtsVoice = value;
        NotifySettingsSave();
    }

    private void NotifySettingsSave() => _coordinator.NotifySettingsModified();

    private void ApplyDucking()
    {
        if (_isDucked) return;
        var player = _coordinator.SourceMediaPlayer;
        if (player == null) return;
        _preDuckTransportVolume = player.Volume;
        player.Volume = _preDuckTransportVolume * Math.Pow(10.0, AudioDuckingDb / 20.0);
        _isDucked = true;
    }

    private void RestoreDucking()
    {
        if (!_isDucked) return;
        var player = _coordinator.SourceMediaPlayer;
        if (player != null) player.Volume = _preDuckTransportVolume;
        _isDucked = false;
    }

    partial void OnIsDubModeOnChanged(bool value)
    {
        if (!value)
        {
            RestoreDucking();
            _coordinator.StopTtsPlayback();
            _lastDubbedSegment = null;
        }
    }

    partial void OnIsFullscreenChanged(bool value)
    {
        if (value)
        {
            _preFullscreenSegmentPaneVisible = IsSegmentPaneVisible;
            IsSegmentPaneVisible = false;
            NotifyControlsActivity();   // show controls briefly then start countdown
        }
        else
        {
            // Don't restore pane if subtitle mode is active — pane stays closed
            IsSegmentPaneVisible = _preFullscreenSegmentPaneVisible && !IsSubtitleModeOn;
            _controlsHideTimer.Stop();
            IsControlsVisible = true;   // always visible outside fullscreen
        }
    }

    partial void OnIsSubtitleModeOnChanged(bool value)
    {
        if (value)
        {
            _preSubtitlePaneState = IsSegmentPaneVisible;
            IsSegmentPaneVisible = false;
            ApplySubtitleState();
        }
        else
        {
            ApplySubtitleState();                           // hide subs before restoring pane
            IsSegmentPaneVisible = _preSubtitlePaneState;
        }
    }

    partial void OnIsSourcePausedChanged(bool value)
    {
        if (value)
        {
            _controlsHideTimer.Stop();
            IsControlsVisible = true;
        }
        else
        {
            NotifyControlsActivity();
        }
    }

    public void NotifyControlsActivity()
    {
        IsControlsVisible = true;
        _controlsHideTimer.Stop();
        // Only auto-hide in fullscreen; controls are always visible in windowed mode
        if (!IsSourcePaused && IsFullscreen)
            _controlsHideTimer.Start();
    }

    private void OnControlsHideTimerTick(object? sender, EventArgs e)
    {
        _controlsHideTimer.Stop();
        IsControlsVisible = false;
    }

    private WorkflowSegmentState? FindSegmentAt(double positionSeconds)
        => Segments.FirstOrDefault(s => positionSeconds >= s.StartSeconds && positionSeconds < s.EndSeconds);

    private void UpdateDubMode()
    {
        var currentSeg = FindSegmentAt(SourcePositionMs / 1000.0);
        if (currentSeg?.SegmentId == _lastDubbedSegment?.SegmentId) return;
        _lastDubbedSegment = currentSeg;
        RestoreDucking();
        _coordinator.StopTtsPlayback();
        if (currentSeg?.HasTtsAudio == true)
        {
            ApplyDucking();
            _ = _coordinator.PlayTtsForSegmentAsync(currentSeg.SegmentId);
        }
    }

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionWorkflowCoordinator.PlaybackState):
                OnPropertyChanged(nameof(PlaybackState));
                break;
            case nameof(SessionWorkflowCoordinator.ActiveTtsSegmentId):
                OnPropertyChanged(nameof(ActiveTtsSegmentId));
                break;
            case nameof(SessionWorkflowCoordinator.BootstrapDiagnostics):
                OnPropertyChanged(nameof(HasDiagnosticsWarning));
                OnPropertyChanged(nameof(DiagnosticsWarningText));
                break;
            case nameof(SessionWorkflowCoordinator.HardwareSnapshot):
                OnPropertyChanged(nameof(HwCpuLine));
                OnPropertyChanged(nameof(HwGpuLine));
                OnPropertyChanged(nameof(HwRamLine));
                OnPropertyChanged(nameof(HwNpuLine));
                OnPropertyChanged(nameof(HwLibsLine));
                break;
            case nameof(SessionWorkflowCoordinator.CurrentSession):
                OnPropertyChanged(nameof(VoiceModelLabel));
                var oldPath = _lastKnownSourceMediaPath;
                var newPath = _coordinator.CurrentSession.SourceMediaPath;
                IsSourceMediaLoaded = !string.IsNullOrEmpty(_coordinator.CurrentSession.IngestedMediaPath);
                
                if (newPath != oldPath)
                {
                    _lastKnownSourceMediaPath = newPath;
                    // Media switched — auto-play triggered by MainWindow code-behind
                    IsSourcePaused = false;
                    _lastDubbedSegment = null;
                    _isUpdatingActiveSegment = true;
                    SelectedSegment = null;
                    _isUpdatingActiveSegment = false;
                }

                if (_coordinator.CurrentSession.Stage >= SessionWorkflowStage.Transcribed)
                {
                    _ = RefreshSegmentsAsync();
                }
                else
                {
                    Segments.Clear();
                    HasSegments = false;
                }
                break;
        }
    }

    [RelayCommand]
    private async Task PlayPauseSourceAsync()
    {
        var player = _coordinator.SourceMediaPlayer;
        if (player == null)
        {
            var ingestedPath = _coordinator.CurrentSession.IngestedMediaPath;
            if (string.IsNullOrEmpty(ingestedPath)) return;
            player = _coordinator.GetOrCreateSourcePlayer();
            player.Load(ingestedPath);
        }

        if (IsSourcePaused)
        {
            try
            {
                await Task.Run(() => player.Play());
                IsSourcePaused = false;
            }
            catch (Exception ex)
            {
                StatusText = $"Play failed: {ex.Message}";
            }
        }
        else
        {
            player.Pause();
            IsSourcePaused = true;
        }
    }

    [RelayCommand]
    private void ToggleDubMode() => IsDubModeOn = !IsDubModeOn;

    [RelayCommand]
    private void SkipBackward()
    {
        if (!HasSegments) return;
        var currentSec = SourcePositionMs / 1000.0;
        var prev = Segments.LastOrDefault(s => s.EndSeconds <= currentSec - 0.1);
        if (prev != null) _ = SeekAndPlayAsync(prev);
    }

    [RelayCommand]
    private void SkipForward()
    {
        if (!HasSegments) return;
        var currentSec = SourcePositionMs / 1000.0;
        var next = Segments.FirstOrDefault(s => s.StartSeconds > currentSec + 0.1);
        if (next != null) _ = SeekAndPlayAsync(next);
    }

    [RelayCommand]
    private void ToggleMute()
    {
        if (IsMuted)
        {
            IsMuted = false;
            SourceVolume = _preMuteVolume > 0 ? _preMuteVolume : 1.0;
        }
        else
        {
            _preMuteVolume = SourceVolume;
            IsMuted = true;
            SourceVolume = 0;
        }
    }

    [RelayCommand]
    private void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    [RelayCommand]
    private void ToggleSegmentPane()
    {
        if (IsSubtitleModeOn)
        {
            // Pull tab while subtitles active → exit subtitle mode; hook restores pane
            IsSubtitleModeOn = false;
        }
        else
        {
            IsSegmentPaneVisible = !IsSegmentPaneVisible;
        }
    }

    [RelayCommand]
    private void ToggleSubtitles()
    {
        if (!HasSegments) return;
        IsSubtitleModeOn = !IsSubtitleModeOn;
    }

    /// <summary>
    /// Called from code-behind after each embedded.Load() to re-apply an active subtitle
    /// track, since mpv clears subtitle tracks when a new file is loaded.
    /// </summary>
    public void ReapplySubtitlesIfActive()
    {
        if (IsSubtitleModeOn && _activeSrtPath != null)
            ApplySubtitleState();
    }

    private void ApplySubtitleState()
    {
        if (_coordinator.SourceMediaPlayer is not LibMpvEmbeddedTransport player) return;

        if (IsSubtitleModeOn)
        {
            var srt = SrtGenerator.Generate(Segments);
            _activeSrtPath = Path.Combine(Path.GetTempPath(), $"subs_{Guid.NewGuid():N}.srt");
            File.WriteAllText(_activeSrtPath, srt, Encoding.UTF8);
            player.RemoveAllSubtitleTracks();
            player.LoadSubtitleTrack(_activeSrtPath);
            player.SubtitlesVisible = true;
        }
        else
        {
            player.SubtitlesVisible = false;
        }
    }

    [RelayCommand]
    private async Task RefreshSegmentsAsync()
    {
        try
        {
            var list = await _coordinator.GetSegmentWorkflowListAsync();
            Segments = new ObservableCollection<WorkflowSegmentState>(list);
            HasSegments = Segments.Count > 0;
            StatusText = HasSegments
                ? $"{Segments.Count} segments loaded."
                : "No segments available. Run the workflow first.";
            if (IsSubtitleModeOn) ApplySubtitleState();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load segments: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PlaySourceAtSegmentAsync(WorkflowSegmentState? segment)
    {
        if (segment is null) return;
        try
        {
            StatusText = $"Playing source at {segment.StartSeconds:F1}s…";
            await _coordinator.PlaySourceMediaAtSegmentAsync(segment.SegmentId);
            IsSourcePaused = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Source playback failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void StopPlayback()
    {
        _coordinator.StopPlayback();
        _lastDubbedSegment = null;
        IsSourcePaused = true;
        StatusText = "Playback stopped.";
    }

    private CancellationTokenSource? _pipelineCts;

    [RelayCommand]
    private void CancelPipeline()
    {
        if (_pipelineCts != null)
        {
            _pipelineCts.Cancel();
            StatusText = "Canceling pipeline...";
            IsPipelineProgressVisible = false;
        }
    }

    [RelayCommand]
    private async Task RunPipelineAsync()
    {
        var diag = _coordinator.BootstrapDiagnostics;
        if (!diag.AllDependenciesAvailable)
        {
            StatusText = $"⚠ {diag.DiagnosticSummary}";
            return;
        }

        // Smart wipe: determine minimum reset point based on what settings changed,
        // then reset and fall through into the run path.
        if (_coordinator.CurrentSession.Stage >= SessionWorkflowStage.Transcribed)
        {
            var cs = _coordinator.CurrentSession;
            var s = _coordinator.CurrentSettings;

            bool wipeTranscription = cs.TranscriptionProvider != s.TranscriptionProvider
                || cs.TranscriptionModel != s.TranscriptionModel;
            bool wipeTranslation = wipeTranscription
                || cs.TranslationProvider != s.TranslationProvider
                || cs.TranslationModel != s.TranslationModel
                || cs.TargetLanguage != s.TargetLanguage;
            bool wipeTts = wipeTranslation
                || cs.TtsProvider != s.TtsProvider
                || cs.TtsVoice != s.TtsVoice;

            if (IsSubtitleModeOn) ToggleSubtitles();
            if (IsDubModeOn) ToggleDubMode();

            if (wipeTranscription)
            {
                StatusText = "Transcription settings changed — restarting from scratch…";
                _coordinator.ResetPipelineToMediaLoaded();
                Segments.Clear();
                HasSegments = false;
            }
            else if (wipeTranslation)
            {
                StatusText = "Translation settings changed — re-running translation…";
                _coordinator.ResetPipelineToTranscribed();
                Segments.Clear();
                HasSegments = false;
                await RefreshSegmentsAsync();
            }
            else if (wipeTts)
            {
                StatusText = "TTS settings changed — re-generating audio…";
                _coordinator.ResetPipelineToTranslated();
            }
            else
            {
                // Nothing changed — re-run everything from scratch
                StatusText = "Re-running full pipeline…";
                _coordinator.ResetPipelineToMediaLoaded();
                Segments.Clear();
                HasSegments = false;
            }
        }

        _pipelineCts?.Cancel();
        _pipelineCts = new CancellationTokenSource();
        var ct = _pipelineCts.Token;

        var progress = new Progress<double>(p =>
        {
            PipelineProgressPercent = p;
            IsPipelineProgressVisible = p > 0;
        });

        try
        {
            IsBusy = true;
            var stage = _coordinator.CurrentSession.Stage;
            System.Diagnostics.Debug.WriteLine($"[Pipeline] Starting. Current stage: {stage}, SourceLanguage: {_coordinator.CurrentSession.SourceLanguage ?? "(null)"}");

            if (stage < SessionWorkflowStage.Transcribed
                || _coordinator.CurrentSession.SourceLanguage is null or "unknown")
            {
                StatusText = "Transcribing audio…";
                System.Diagnostics.Debug.WriteLine("[Pipeline] Running transcription…");
                await _coordinator.TranscribeMediaAsync(ct, progress);
                System.Diagnostics.Debug.WriteLine($"[Pipeline] Transcription done. Language: {_coordinator.CurrentSession.SourceLanguage}");
                StatusText = "Transcription complete. Loading segments…";
                await RefreshSegmentsAsync();
                System.Diagnostics.Debug.WriteLine($"[Pipeline] Segments loaded after transcription: {Segments.Count}");
                StatusText = $"Transcribed {Segments.Count} segments. Ready for translation.";
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Pipeline] Skipping transcription (already done).");
            }

            stage = _coordinator.CurrentSession.Stage;
            if (stage < SessionWorkflowStage.Translated)
            {
                StatusText = $"Translating ({_coordinator.CurrentSession.SourceLanguage} → en)…";
                System.Diagnostics.Debug.WriteLine("[Pipeline] Running translation…");
                await _coordinator.TranslateTranscriptAsync(ct, progress);
                System.Diagnostics.Debug.WriteLine("[Pipeline] Translation done.");
                StatusText = "Translation complete. Loading segments…";
                await RefreshSegmentsAsync();
                System.Diagnostics.Debug.WriteLine($"[Pipeline] Segments loaded after translation: {Segments.Count}");
                StatusText = $"Translated {Segments.Count} segments. Ready for TTS.";
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Pipeline] Skipping translation (already done).");
            }

            stage = _coordinator.CurrentSession.Stage;
            if (stage < SessionWorkflowStage.TtsGenerated)
            {
                StatusText = "Generating dubbed audio…";
                System.Diagnostics.Debug.WriteLine("[Pipeline] Running TTS…");
                await _coordinator.GenerateTtsAsync(ct, progress);
                System.Diagnostics.Debug.WriteLine("[Pipeline] TTS done.");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Pipeline] Skipping TTS (already done).");
            }

            StatusText = "Loading segments…";
            await RefreshSegmentsAsync();
            System.Diagnostics.Debug.WriteLine($"[Pipeline] Complete. {Segments.Count} segments loaded.");
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("[Pipeline] Cancelled by user.");
            StatusText = "Pipeline cancelled.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Pipeline] Failed: {ex}");
            StatusText = $"Pipeline failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsPipelineProgressVisible = false;
            _pipelineCts?.Dispose();
            _pipelineCts = null;
        }
    }

    [RelayCommand]
    private async Task RegenerateTranslationAsync(WorkflowSegmentState? segment)
    {
        if (segment is null) return;
        try
        {
            IsBusy = true;
            StatusText = $"Regenerating translation for {segment.SegmentId}…";
            await _coordinator.RegenerateSegmentTranslationAsync(segment.SegmentId);
            await RefreshSegmentsAsync();
            StatusText = $"Translation regenerated for {segment.SegmentId}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Regeneration failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
