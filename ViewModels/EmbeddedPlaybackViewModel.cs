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
using Babel.Player.Services.Registries;

namespace Babel.Player.ViewModels;

public partial class EmbeddedPlaybackViewModel : ViewModelBase
{
    private readonly SessionWorkflowCoordinator _coordinator;
    private readonly ApiKeyStore? _apiKeyStore;
    private readonly IErrorDialogService? _errorDialogService;
    private readonly string? _logFilePath;
    private string? _lastKnownSourceMediaPath;
    private bool _isUpdatingPositionFromTimer;
    private bool _isUpdatingActiveSegment;
    private bool _isSynchronizingPipelineSettings;
    private WorkflowSegmentState? _lastDubbedSegment;
    private CancellationTokenSource? _providerReadinessRefreshCts;
    private int _providerReadinessRefreshVersion;

    [ObservableProperty]
    private ObservableCollection<WorkflowSegmentState> _segments = [];

    // Parallel sorted array for O(log n) seek — rebuilt whenever Segments is replaced.
    private WorkflowSegmentState[] _sortedSegments = [];

    [ObservableProperty]
    private WorkflowSegmentState? _selectedSegment;

    [ObservableProperty]
    private bool _hasSegments;

    [ObservableProperty]
    private string _statusText = "No segments loaded.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetails))]
    private string? _statusErrorTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetails))]
    private string? _statusErrorDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunPipeline))]
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
    private string _transcriptionProvider = ProviderNames.FasterWhisper;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTranscriptionModel))]
    [NotifyPropertyChangedFor(nameof(TranscriptionKeyStatus))]
    private string _transcriptionModel = "base";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableTranslationModels))]
    [NotifyPropertyChangedFor(nameof(SelectedTranslationModel))]
    [NotifyPropertyChangedFor(nameof(TranslationKeyStatus))]
    private string _translationProvider = ProviderNames.GoogleTranslateFree;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTranslationModel))]
    [NotifyPropertyChangedFor(nameof(TranslationKeyStatus))]
    private string _translationModel = "default";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableTtsOptions))]
    [NotifyPropertyChangedFor(nameof(SelectedTtsOption))]
    [NotifyPropertyChangedFor(nameof(TtsKeyStatus))]
    private string _ttsProvider = ProviderNames.EdgeTts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTtsOption))]
    [NotifyPropertyChangedFor(nameof(TtsKeyStatus))]
    private string _ttsModelOrVoice = "en-US-AriaNeural";

    // ── Multi-speaker routing controls ───────────────────────────────────────
    [ObservableProperty]
    private bool _isMultiSpeakerEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpeakers))]
    private ObservableCollection<string> _speakerIds = [];

    [ObservableProperty]
    private string? _selectedSpeakerId;

    [ObservableProperty]
    private string _selectedSpeakerAssignedVoice = "";

    [ObservableProperty]
    private string _selectedSpeakerReferenceAudioPath = "";

    private double _preMuteVolume = 1.0;
    private double _preDuckTransportVolume = 1.0;
    private bool _isDucked;
    private bool _preFullscreenSegmentPaneVisible = true;
    private bool _preSubtitlePaneState = true;
    private string? _activeSrtPath;
    private IReadOnlyList<ModelOptionViewModel> _availableTranscriptionModels = [];
    private IReadOnlyList<ModelOptionViewModel> _availableTranslationModels = [];
    private IReadOnlyList<ModelOptionViewModel> _availableTtsOptions = [];
    private string _transcriptionKeyStatus = string.Empty;
    private string _translationKeyStatus = string.Empty;
    private string _ttsKeyStatus = string.Empty;

    [ObservableProperty]
    private ModelOptionViewModel? _selectedTranscriptionModel;

    [ObservableProperty]
    private ModelOptionViewModel? _selectedTranslationModel;

    [ObservableProperty]
    private ModelOptionViewModel? _selectedTtsOption;

    private readonly DispatcherTimer _controlsHideTimer;
    private const int ControlsHideDelayMs = 3000;
    private const double PositionUpdateThresholdMs = 0.5;

    public EmbeddedPlaybackViewModel(
        SessionWorkflowCoordinator coordinator,
        ApiKeyStore? apiKeyStore = null,
        IErrorDialogService? errorDialogService = null,
        string? logFilePath = null)
    {
        _coordinator = coordinator;
        _apiKeyStore = apiKeyStore;
        _errorDialogService = errorDialogService;
        _logFilePath = logFilePath;
        _lastKnownSourceMediaPath = coordinator.CurrentSession.SourceMediaPath;
        _isSourceMediaLoaded = !string.IsNullOrEmpty(coordinator.CurrentSession.IngestedMediaPath);
        SyncProviderModelFieldsFromSettings();

        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
        _coordinator.SettingsModified += OnCoordinatorSettingsModified;

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
    public bool CanRunPipeline => !IsBusy;
    public bool HasErrorDetails => !string.IsNullOrWhiteSpace(StatusErrorDetail);
    public bool HasDiagnosticsWarning => !_coordinator.BootstrapDiagnostics.AllDependenciesAvailable;
    public string DiagnosticsWarningText => _coordinator.BootstrapDiagnostics.DiagnosticSummary;
    public string VoiceModelLabel => _coordinator.CurrentSession.TtsVoice ?? _coordinator.CurrentSettings.TtsVoice;
    public string ActiveTranscriptionConfigLine => $"{TranscriptionProvider} / {TranscriptionModel}";
    public string ActiveCpuTuningLine
    {
        get
        {
            var s = _coordinator.CurrentSettings;
            var threads = s.TranscriptionCpuThreads > 0 ? s.TranscriptionCpuThreads.ToString() : "auto";
            return $"{s.TranscriptionCpuComputeType} · threads {threads} · workers {Math.Max(1, s.TranscriptionNumWorkers)}";
        }
    }
    public string ActiveTranslationConfigLine =>
        $"{TranslationProvider} / {TranslationModel} · target {_coordinator.CurrentSettings.TargetLanguage}";
    public string ActiveTtsConfigLine => $"{TtsProvider} / {TtsModelOrVoice}";
    public string SelectedSegmentSpeakerId => SelectedSegment?.SpeakerId ?? "—";
    public string SelectedSegmentAssignedVoice => SelectedSegment?.AssignedVoice ?? "—";
    public string SelectedSegmentReferenceStatus => SelectedSegment?.HasReferenceAudio == true ? "Yes" : "No";
    public bool HasSpeakers => SpeakerIds.Count > 0;

    // ── Provider / model option lists ──────────────────────────────────────────
    public IReadOnlyList<string> TranscriptionProviders => [.. _coordinator.TranscriptionRegistry.GetAvailableProviders().Where(p => p.IsImplemented).Select(p => p.Id)];
    public IReadOnlyList<string> TranslationProviders => [.. _coordinator.TranslationRegistry.GetAvailableProviders().Where(p => p.IsImplemented).Select(p => p.Id)];
    public IReadOnlyList<string> TtsProviders => [.. _coordinator.TtsRegistry.GetAvailableProviders().Where(p => p.IsImplemented).Select(p => p.Id)];

    public IReadOnlyList<ModelOptionViewModel> AvailableTranscriptionModels => _availableTranscriptionModels;

    public IReadOnlyList<ModelOptionViewModel> AvailableTranslationModels => _availableTranslationModels;

    public IReadOnlyList<ModelOptionViewModel> AvailableTtsOptions => _availableTtsOptions;

    // ── Pipeline Progress ──────────────────────────────────────────────────────
    [ObservableProperty]
    private double _pipelineProgressPercent;

    [ObservableProperty]
    private bool _isPipelineProgressVisible;

    // ── API key / readiness status for UI lock indicators ──────────────────────
    public string TranscriptionKeyStatus => _transcriptionKeyStatus;

    public string TranslationKeyStatus => _translationKeyStatus;

    public string TtsKeyStatus => _ttsKeyStatus;

    private sealed record ProviderSelectionSnapshot(
        string TranscriptionProvider,
        string TranscriptionModel,
        string TranslationProvider,
        string TranslationModel,
        string TtsProvider,
        string TtsModelOrVoice);

    private sealed record ProviderReadinessStatus(
        string TranscriptionStatus,
        string TranslationStatus,
        string TtsStatus);

    private static string GetReadinessStatus(ProviderReadiness readiness)
    {
        if (readiness.IsReady) return "";
        if (readiness.RequiresModelDownload) return "⬇️ Download required (will run automatically)";
        return $"⚠️ {readiness.BlockingReason}";
    }

    private void RefreshProviderReadinessStatuses()
    {
        var snapshot = new ProviderSelectionSnapshot(
            TranscriptionProvider,
            TranscriptionModel,
            TranslationProvider,
            TranslationModel,
            TtsProvider,
            TtsModelOrVoice);
        QueueProviderReadinessRefresh(snapshot);
    }

    private void QueueProviderReadinessRefresh(ProviderSelectionSnapshot snapshot)
    {
        var version = Interlocked.Increment(ref _providerReadinessRefreshVersion);
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _providerReadinessRefreshCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        _coordinator.Log.Info(
            $"Provider readiness refresh queued: v={version}, " +
            $"selection=({snapshot.TranscriptionProvider}/{snapshot.TranscriptionModel}, " +
            $"{snapshot.TranslationProvider}/{snapshot.TranslationModel}, " +
            $"{snapshot.TtsProvider}/{snapshot.TtsModelOrVoice})");

        _ = RefreshProviderReadinessStatusesAsync(snapshot, version, cts.Token);
    }

    private async Task RefreshProviderReadinessStatusesAsync(
        ProviderSelectionSnapshot snapshot,
        int version,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var status = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ProviderReadinessStatus(
                    GetReadinessStatus(_coordinator.TranscriptionRegistry.CheckReadiness(
                        snapshot.TranscriptionProvider,
                        snapshot.TranscriptionModel,
                        _coordinator.CurrentSettings,
                        _apiKeyStore)),
                    GetReadinessStatus(_coordinator.TranslationRegistry.CheckReadiness(
                        snapshot.TranslationProvider,
                        snapshot.TranslationModel,
                        _coordinator.CurrentSettings,
                        _apiKeyStore)),
                    GetReadinessStatus(_coordinator.TtsRegistry.CheckReadiness(
                        snapshot.TtsProvider,
                        snapshot.TtsModelOrVoice,
                        _coordinator.CurrentSettings,
                        _apiKeyStore)));
            }, cancellationToken);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _providerReadinessRefreshVersion || cancellationToken.IsCancellationRequested)
                    return;

                ApplyReadinessStatus(ref _transcriptionKeyStatus, status.TranscriptionStatus, nameof(TranscriptionKeyStatus));
                ApplyReadinessStatus(ref _translationKeyStatus, status.TranslationStatus, nameof(TranslationKeyStatus));
                ApplyReadinessStatus(ref _ttsKeyStatus, status.TtsStatus, nameof(TtsKeyStatus));
            });

            stopwatch.Stop();
            _coordinator.Log.Info(
                $"Provider readiness refresh complete: v={version}, elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _coordinator.Log.Info(
                $"Provider readiness refresh canceled: v={version}, elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _coordinator.Log.Error(
                $"Provider readiness refresh failed: v={version}, elapsedMs={stopwatch.ElapsedMilliseconds}",
                ex);
        }
    }

    private void ApplyReadinessStatus(ref string field, string value, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
            return;

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void ClearStatusErrorDetail()
    {
        StatusErrorTitle = null;
        StatusErrorDetail = null;
    }

    private void SetStatusErrorDetail(string title, Exception ex)
    {
        StatusErrorTitle = title;
        StatusErrorDetail = ex.ToString();
    }

    // ── Hardware display lines ─────────────────────────────────────────────────
    public string HwCpuLine => _coordinator.HardwareSnapshot.CpuLine;
    public string HwGpuLine => _coordinator.HardwareSnapshot.GpuLine;
    public string HwRamLine => _coordinator.HardwareSnapshot.RamLine;
    public string HwNpuLine => _coordinator.HardwareSnapshot.NpuLine;
    public string HwLibsLine => _coordinator.HardwareSnapshot.LibsLine;
    public string HwInferenceLine => _coordinator.BootstrapDiagnostics.InferenceLine;

    public string SourcePositionFormatted => FormatMs(SourcePositionMs);
    public string SourceDurationFormatted => FormatMs(SourceDurationMs);

    private static string FormatMs(double ms) =>
        ms <= 0 ? "0:00" : TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss");

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        var player = _coordinator.SourceMediaPlayer;
        if (player == null || player.Duration == 0) return;

        _isUpdatingPositionFromTimer = true;
        var newDurationMs = player.Duration;
        if (Math.Abs(SourceDurationMs - newDurationMs) > PositionUpdateThresholdMs)
            SourceDurationMs = newDurationMs;

        var newPositionMs = player.CurrentTime;
        if (Math.Abs(SourcePositionMs - newPositionMs) > PositionUpdateThresholdMs)
            SourcePositionMs = newPositionMs;
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
        OnPropertyChanged(nameof(SelectedSegmentSpeakerId));
        OnPropertyChanged(nameof(SelectedSegmentAssignedVoice));
        OnPropertyChanged(nameof(SelectedSegmentReferenceStatus));

        if (!string.IsNullOrWhiteSpace(value?.SpeakerId) && SpeakerIds.Contains(value.SpeakerId))
            SelectedSpeakerId = value.SpeakerId;

        if (_isUpdatingActiveSegment || value == null || !IsSourceMediaLoaded) return;
        _ = SeekAndPlayAsync(value);
    }

    partial void OnSelectedSpeakerIdChanged(string? value)
    {
        UpdateSelectedSpeakerDetails(value);
    }

    partial void OnIsMultiSpeakerEnabledChanged(bool value)
    {
        _coordinator.SetMultiSpeakerEnabled(value);
        _ = RefreshSegmentsAsync();
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
                ClearStatusErrorDetail();
            }
            catch (Exception ex)
            {
                StatusText = $"Play failed: {ex.Message}";
                SetStatusErrorDetail("Source Playback failed", ex);
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
        if (_isSynchronizingPipelineSettings || string.IsNullOrEmpty(value)) return;

        ApplyPipelineSettingsSelection(new PipelineSettingsSelection(
            value,
            GetDefaultModelId(_coordinator.TranscriptionRegistry.GetAvailableProviders(), value),
            TranslationProvider,
            TranslationModel,
            TtsProvider,
            TtsModelOrVoice,
            _coordinator.CurrentSettings.TargetLanguage));
    }

    partial void OnTranscriptionModelChanged(string value)
    {
        if (_isSynchronizingPipelineSettings || string.IsNullOrEmpty(value)) return;

        ApplyPipelineSettingsSelection(new PipelineSettingsSelection(
            TranscriptionProvider,
            value,
            TranslationProvider,
            TranslationModel,
            TtsProvider,
            TtsModelOrVoice,
            _coordinator.CurrentSettings.TargetLanguage));
    }

    partial void OnSelectedTranscriptionModelChanged(ModelOptionViewModel? value)
    {
        if (value is null || value.ModelId == TranscriptionModel) return;
        TranscriptionModel = value.ModelId;
    }

    partial void OnSelectedTranslationModelChanged(ModelOptionViewModel? value)
    {
        if (value is null || value.ModelId == TranslationModel) return;
        TranslationModel = value.ModelId;
    }

    partial void OnSelectedTtsOptionChanged(ModelOptionViewModel? value)
    {
        if (value is null || value.ModelId == TtsModelOrVoice) return;
        TtsModelOrVoice = value.ModelId;
    }

    partial void OnTranslationProviderChanged(string value)
    {
        if (_isSynchronizingPipelineSettings || string.IsNullOrEmpty(value)) return;

        ApplyPipelineSettingsSelection(new PipelineSettingsSelection(
            TranscriptionProvider,
            TranscriptionModel,
            value,
            GetDefaultModelId(_coordinator.TranslationRegistry.GetAvailableProviders(), value),
            TtsProvider,
            TtsModelOrVoice,
            _coordinator.CurrentSettings.TargetLanguage));
    }

    partial void OnTranslationModelChanged(string value)
    {
        if (_isSynchronizingPipelineSettings || string.IsNullOrEmpty(value)) return;

        ApplyPipelineSettingsSelection(new PipelineSettingsSelection(
            TranscriptionProvider,
            TranscriptionModel,
            TranslationProvider,
            value,
            TtsProvider,
            TtsModelOrVoice,
            _coordinator.CurrentSettings.TargetLanguage));
    }

    partial void OnTtsProviderChanged(string value)
    {
        if (_isSynchronizingPipelineSettings || string.IsNullOrEmpty(value)) return;

        ApplyPipelineSettingsSelection(new PipelineSettingsSelection(
            TranscriptionProvider,
            TranscriptionModel,
            TranslationProvider,
            TranslationModel,
            value,
            GetDefaultModelId(_coordinator.TtsRegistry.GetAvailableProviders(), value),
            _coordinator.CurrentSettings.TargetLanguage));
    }

    partial void OnTtsModelOrVoiceChanged(string value)
    {
        if (_isSynchronizingPipelineSettings || string.IsNullOrEmpty(value)) return;

        ApplyPipelineSettingsSelection(new PipelineSettingsSelection(
            TranscriptionProvider,
            TranscriptionModel,
            TranslationProvider,
            TranslationModel,
            TtsProvider,
            value,
            _coordinator.CurrentSettings.TargetLanguage));
    }

    private void OnCoordinatorSettingsModified()
    {
        SyncProviderModelFieldsFromSettings();
        NotifyActiveConfigChanged();
        OnPropertyChanged(nameof(VoiceModelLabel));
    }

    /// <summary>
    /// Synchronizes all provider/model UI backing fields from CurrentSettings.
    /// Called when CurrentSession changes (e.g., media restored from cache).
    /// This ensures dropdowns always display the actual configured state,
    /// not stale values from a previous session.
    /// </summary>
    private void SyncProviderModelFieldsFromSettings()
    {
        _isSynchronizingPipelineSettings = true;
        try
        {
            TranscriptionProvider = _coordinator.CurrentSettings.TranscriptionProvider;
            TranscriptionModel = _coordinator.CurrentSettings.TranscriptionModel;
            TranslationProvider = _coordinator.CurrentSettings.TranslationProvider;
            TranslationModel = _coordinator.CurrentSettings.TranslationModel;
            TtsProvider = _coordinator.CurrentSettings.TtsProvider;
            TtsModelOrVoice = _coordinator.CurrentSettings.TtsVoice;

            RebuildAllModelOptions();

            SelectedTranscriptionModel =
                _availableTranscriptionModels.FirstOrDefault(m => m.ModelId == TranscriptionModel)
                ?? _availableTranscriptionModels.FirstOrDefault();
            SelectedTranslationModel =
                _availableTranslationModels.FirstOrDefault(m => m.ModelId == TranslationModel)
                ?? _availableTranslationModels.FirstOrDefault();
            SelectedTtsOption =
                _availableTtsOptions.FirstOrDefault(m => m.ModelId == TtsModelOrVoice)
                ?? _availableTtsOptions.FirstOrDefault();

            IsMultiSpeakerEnabled = _coordinator.CurrentSession.MultiSpeakerEnabled;
        }
        finally
        {
            _isSynchronizingPipelineSettings = false;
        }

        OnPropertyChanged(nameof(AvailableTranscriptionModels));
        OnPropertyChanged(nameof(AvailableTranslationModels));
        OnPropertyChanged(nameof(AvailableTtsOptions));
        RefreshProviderReadinessStatuses();
        RebuildSpeakerIds();
    }

    private void NotifyActiveConfigChanged()
    {
        OnPropertyChanged(nameof(ActiveTranscriptionConfigLine));
        OnPropertyChanged(nameof(ActiveCpuTuningLine));
        OnPropertyChanged(nameof(ActiveTranslationConfigLine));
        OnPropertyChanged(nameof(ActiveTtsConfigLine));
    }

    private void RebuildAllModelOptions()
    {
        RebuildTranscriptionModelOptions();
        RebuildTranslationModelOptions();
        RebuildTtsModelOptions();
    }

    private void RebuildTranscriptionModelOptions()
    {
        _availableTranscriptionModels =
            [.. (_coordinator.TranscriptionRegistry.GetAvailableProviders()
                    .FirstOrDefault(p => p.Id == TranscriptionProvider)?.SupportedModels ?? ["default"])
                .Select(m => new ModelOptionViewModel(m, GetTranscriptionModelAvailability(TranscriptionProvider, m)))];
    }

    private void RebuildTranslationModelOptions()
    {
        _availableTranslationModels =
            [.. (_coordinator.TranslationRegistry.GetAvailableProviders()
                    .FirstOrDefault(p => p.Id == TranslationProvider)?.SupportedModels ?? ["default"])
                .Select(m => new ModelOptionViewModel(m, GetTranslationModelAvailability(TranslationProvider, m)))];
    }

    private void RebuildTtsModelOptions()
    {
        _availableTtsOptions =
            [.. (_coordinator.TtsRegistry.GetAvailableProviders()
                    .FirstOrDefault(p => p.Id == TtsProvider)?.SupportedModels ?? ["default"])
                .Select(m => new ModelOptionViewModel(m, GetTtsModelAvailability(TtsProvider, m)))];
    }

    private static bool? GetTranscriptionModelAvailability(string providerId, string model) =>
        providerId switch
        {
            ProviderNames.FasterWhisper => ModelDownloader.IsFasterWhisperDownloaded(model),
            _ => null,
        };

    private static bool? GetTranslationModelAvailability(string providerId, string model) =>
        providerId switch
        {
            ProviderNames.Nllb200 => ModelDownloader.IsNllbDownloaded(model),
            _ => null,
        };

    private bool? GetTtsModelAvailability(string providerId, string model) =>
        providerId switch
        {
            ProviderNames.Piper => ModelDownloader.IsPiperVoiceDownloaded(model, _coordinator.CurrentSettings.PiperModelDir),
            _ => null,
        };

    private void ApplyPipelineSettingsSelection(PipelineSettingsSelection selection)
    {
        var result = _coordinator.ApplyPipelineSettings(selection);
        SyncProviderModelFieldsFromSettings();
        NotifyActiveConfigChanged();
        HandlePipelineSettingsApplyResult(result);
    }

    private void HandlePipelineSettingsApplyResult(PipelineSettingsApplyResult result)
    {
        if (!result.SettingsChanged)
            return;

        if (result.Invalidation != PipelineInvalidation.None)
            ResetInteractiveModes();
        StatusText = result.StatusMessage;
        ClearStatusErrorDetail();

        if (_coordinator.CurrentSession.Stage >= SessionWorkflowStage.Transcribed)
        {
            _ = RefreshSegmentsAsync();
        }
        else
        {
            Segments.Clear();
            HasSegments = false;
        }
    }

    private void ResetInteractiveModes()
    {
        if (IsSubtitleModeOn)
            IsSubtitleModeOn = false;
        if (IsDubModeOn)
            IsDubModeOn = false;
    }

    private static string GetDefaultModelId(
        IReadOnlyList<ProviderDescriptor> providers,
        string providerId) =>
        providers.FirstOrDefault(provider => provider.Id == providerId)?.SupportedModels.FirstOrDefault()
        ?? "default";

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

    partial void OnSegmentsChanged(ObservableCollection<WorkflowSegmentState> value)
    {
        var arr = value.ToArray();
        Array.Sort(arr, (a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        _sortedSegments = arr;
        RebuildSpeakerIds();
    }

    private void RebuildSpeakerIds()
    {
        var ordered = Segments
            .Select(segment => segment.SpeakerId)
            .Where(speakerId => !string.IsNullOrWhiteSpace(speakerId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(speakerId => speakerId, StringComparer.Ordinal)
            .ToList();

        SpeakerIds = new ObservableCollection<string>(ordered!);
        OnPropertyChanged(nameof(HasSpeakers));

        if (selectedSpeakerInvalid())
        {
            SelectedSpeakerId = SpeakerIds.FirstOrDefault();
        }
        else
        {
            UpdateSelectedSpeakerDetails(SelectedSpeakerId);
        }

        bool selectedSpeakerInvalid() => string.IsNullOrWhiteSpace(SelectedSpeakerId) || !SpeakerIds.Contains(SelectedSpeakerId);
    }

    private void UpdateSelectedSpeakerDetails(string? speakerId)
    {
        if (string.IsNullOrWhiteSpace(speakerId))
        {
            SelectedSpeakerAssignedVoice = string.Empty;
            SelectedSpeakerReferenceAudioPath = string.Empty;
            return;
        }

        var voiceMap = _coordinator.GetSpeakerVoiceAssignments();
        var refMap = _coordinator.GetSpeakerReferenceAudioPaths();

        SelectedSpeakerAssignedVoice = voiceMap.TryGetValue(speakerId, out var voice)
            ? voice
            : string.Empty;
        SelectedSpeakerReferenceAudioPath = refMap.TryGetValue(speakerId, out var path)
            ? path
            : string.Empty;
    }

    private WorkflowSegmentState? FindSegmentAt(double positionSeconds)
    {
        var arr = _sortedSegments;
        if (arr.Length == 0) return null;
        // Binary search for the last segment with StartSeconds <= positionSeconds.
        int lo = 0, hi = arr.Length - 1, candidate = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (arr[mid].StartSeconds <= positionSeconds) { candidate = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (candidate < 0) return null;
        var seg = arr[candidate];
        return positionSeconds < seg.EndSeconds ? seg : null;
    }

    private WorkflowSegmentState? FindPreviousSegmentEndingBefore(double positionSeconds)
    {
        var arr = _sortedSegments;
        if (arr.Length == 0)
            return null;

        int lo = 0, hi = arr.Length - 1, candidate = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (arr[mid].EndSeconds <= positionSeconds)
            {
                candidate = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return candidate >= 0 ? arr[candidate] : null;
    }

    private WorkflowSegmentState? FindNextSegmentStartingAfter(double positionSeconds)
    {
        var arr = _sortedSegments;
        if (arr.Length == 0)
            return null;

        int lo = 0, hi = arr.Length - 1, candidate = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (arr[mid].StartSeconds > positionSeconds)
            {
                candidate = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return candidate >= 0 ? arr[candidate] : null;
    }

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
                OnPropertyChanged(nameof(HwInferenceLine));
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
                    IsSourcePaused = true;
                    _lastDubbedSegment = null;
                    _isUpdatingActiveSegment = true;
                    SelectedSegment = null;
                    _isUpdatingActiveSegment = false;
                }

                // Sync provider/model UI fields from CurrentSettings when session changes.
                // This ensures dropdowns always reflect the actual configured state,
                // especially after session restore from cache.
                SyncProviderModelFieldsFromSettings();
                NotifyActiveConfigChanged();

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
                ClearStatusErrorDetail();
            }
            catch (Exception ex)
            {
                StatusText = $"Play failed: {ex.Message}";
                SetStatusErrorDetail("Source Playback failed", ex);
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
        var prev = FindPreviousSegmentEndingBefore(currentSec - 0.1);
        if (prev != null) _ = SeekAndPlayAsync(prev);
    }

    [RelayCommand]
    private void SkipForward()
    {
        if (!HasSegments) return;
        var currentSec = SourcePositionMs / 1000.0;
        var next = FindNextSegmentStartingAfter(currentSec + 0.1);
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

    private void DeleteActiveSubtitleFile()
    {
        if (string.IsNullOrEmpty(_activeSrtPath))
            return;

        try
        {
            if (File.Exists(_activeSrtPath))
                File.Delete(_activeSrtPath);
        }
        catch
        {
            // Best-effort cleanup only.
        }
        finally
        {
            _activeSrtPath = null;
        }
    }

    private void ApplySubtitleState()
    {
        if (_coordinator.SourceMediaPlayer is not LibMpvEmbeddedTransport player) return;

        if (IsSubtitleModeOn)
        {
            var srt = SrtGenerator.Generate(Segments);
            DeleteActiveSubtitleFile();
            _activeSrtPath = Path.Combine(Path.GetTempPath(), $"subs_{Guid.NewGuid():N}.srt");
            File.WriteAllText(_activeSrtPath, srt, Encoding.UTF8);
            player.RemoveAllSubtitleTracks();
            player.LoadSubtitleTrack(_activeSrtPath);
            player.SubtitlesVisible = true;
        }
        else
        {
            player.SubtitlesVisible = false;
            DeleteActiveSubtitleFile();
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
            ClearStatusErrorDetail();
            if (IsSubtitleModeOn) ApplySubtitleState();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load segments: {ex.Message}";
            SetStatusErrorDetail("Load Segments failed", ex);
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
            ClearStatusErrorDetail();
        }
        catch (Exception ex)
        {
            StatusText = $"Source playback failed: {ex.Message}";
            SetStatusErrorDetail("Source Playback failed", ex);
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
            ClearStatusErrorDetail();
        }
    }

    [RelayCommand]
    private async Task RunPipelineAsync()
    {
        var diag = _coordinator.BootstrapDiagnostics;
        if (!diag.AllDependenciesAvailable)
        {
            StatusText = $"⚠ {diag.DiagnosticSummary}";
            ClearStatusErrorDetail();
            return;
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
            StatusText = "Running pipeline…";
            ClearStatusErrorDetail();
            await _coordinator.AdvancePipelineAsync(progress, ct);
            StatusText = "Loading segments…";
            await RefreshSegmentsAsync();
            StatusText = _coordinator.CurrentSession.StatusMessage;
            ClearStatusErrorDetail();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Pipeline cancelled.";
            ClearStatusErrorDetail();
        }
        catch (Exception ex)
        {
            StatusText = $"Pipeline failed: {ex.Message}";
            SetStatusErrorDetail("Pipeline failed", ex);
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
    private void ClearPipeline()
    {
        _coordinator.ClearPipeline();
        Segments.Clear();
        HasSegments = false;
        ResetInteractiveModes();
        StatusText = "Pipeline cleared. Ready to run fresh.";
        ClearStatusErrorDetail();
    }

    [RelayCommand]
    private async Task RegenerateTranslationAsync(WorkflowSegmentState? segment)
    {
        if (segment is null) return;
        try
        {
            IsBusy = true;
            StatusText = $"Regenerating translation for {segment.SegmentId}…";
            ClearStatusErrorDetail();
            await _coordinator.RegenerateSegmentTranslationAsync(segment.SegmentId);
            await RefreshSegmentsAsync();
            StatusText = $"Translation regenerated for {segment.SegmentId}.";
            ClearStatusErrorDetail();
        }
        catch (Exception ex)
        {
            StatusText = $"Regeneration failed: {ex.Message}";
            SetStatusErrorDetail("Regeneration failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ShowStatusErrorDetailsAsync()
    {
        if (_errorDialogService is null || string.IsNullOrWhiteSpace(StatusErrorDetail))
        {
            return;
        }

        await _errorDialogService.ShowErrorAsync(
            StatusErrorTitle ?? "Error details",
            StatusErrorDetail,
            _logFilePath);
    }

    [RelayCommand]
    private async Task AssignSelectedSpeakerVoiceAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedSpeakerId) || string.IsNullOrWhiteSpace(TtsModelOrVoice))
            return;

        _coordinator.SetSpeakerVoiceAssignment(SelectedSpeakerId, TtsModelOrVoice);
        StatusText = $"Assigned {TtsModelOrVoice} to {SelectedSpeakerId}.";
        UpdateSelectedSpeakerDetails(SelectedSpeakerId);
        await RefreshSegmentsAsync();
    }

    [RelayCommand]
    private async Task ClearSelectedSpeakerVoiceAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedSpeakerId))
            return;

        _coordinator.RemoveSpeakerVoiceAssignment(SelectedSpeakerId);
        StatusText = $"Cleared voice assignment for {SelectedSpeakerId}.";
        UpdateSelectedSpeakerDetails(SelectedSpeakerId);
        await RefreshSegmentsAsync();
    }
}
