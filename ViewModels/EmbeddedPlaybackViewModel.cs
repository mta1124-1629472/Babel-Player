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
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;

namespace Babel.Player.ViewModels;

public partial class EmbeddedPlaybackViewModel : ViewModelBase, IDisposable
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
    private readonly Dictionary<ComputeProfile, IReadOnlyList<ProviderDescriptor>> _transcriptionProvidersByRuntime = [];
    private readonly Dictionary<ComputeProfile, IReadOnlyList<ProviderDescriptor>> _translationProvidersByRuntime = [];
    private readonly Dictionary<ComputeProfile, IReadOnlyList<ProviderDescriptor>> _ttsProvidersByRuntime = [];
    private readonly Dictionary<ComputeProfile, IReadOnlyList<string>> _transcriptionProviderIdsByRuntime = [];
    private readonly Dictionary<ComputeProfile, IReadOnlyList<string>> _translationProviderIdsByRuntime = [];
    private readonly Dictionary<ComputeProfile, IReadOnlyList<string>> _ttsProviderIdsByRuntime = [];
    private readonly ObservableCollection<ProviderHealthSnapshot> _providerHealthSnapshots = [];
    private readonly Dictionary<string, Queue<string>> _providerHealthHistoryByKey = new(StringComparer.Ordinal);
    private readonly object _providerHealthHistoryLock = new();
    private CancellationTokenSource? _providerHealthRefreshCts;
    private int _providerHealthRefreshVersion;
    private ProviderDiagnosticsSelectionSnapshot? _lastQueuedProviderHealthSnapshot;

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
    [NotifyPropertyChangedFor(nameof(CanRunDiarizationOnly))]
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
    [NotifyPropertyChangedFor(nameof(IsPanePullTabVisible))]
    private bool _isFullscreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SegmentPaneToggleLabel))]
    [NotifyPropertyChangedFor(nameof(IsPanePullTabVisible))]
    private bool _isSegmentPaneVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPullTabVisible))]
    [NotifyPropertyChangedFor(nameof(IsPanePullTabVisible))]
    private bool _isControlsVisible = true;

    [ObservableProperty]
    private string _selectedPlaybackRate = "1x";

    [ObservableProperty]
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
    [NotifyPropertyChangedFor(nameof(TranscriptionProviders))]
    [NotifyPropertyChangedFor(nameof(AvailableTranscriptionModels))]
    [NotifyPropertyChangedFor(nameof(SelectedTranscriptionModel))]
    [NotifyPropertyChangedFor(nameof(TranscriptionKeyStatus))]
    private ComputeProfile _transcriptionRuntime = ComputeProfile.Cpu;

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
    [NotifyPropertyChangedFor(nameof(TranslationProviders))]
    [NotifyPropertyChangedFor(nameof(AvailableTranslationModels))]
    [NotifyPropertyChangedFor(nameof(SelectedTranslationModel))]
    [NotifyPropertyChangedFor(nameof(TranslationKeyStatus))]
    private ComputeProfile _translationRuntime = ComputeProfile.Cloud;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableTranslationModels))]
    [NotifyPropertyChangedFor(nameof(SelectedTranslationModel))]
    [NotifyPropertyChangedFor(nameof(TranslationKeyStatus))]
    private string _translationProvider = ProviderNames.Deepl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTranslationModel))]
    [NotifyPropertyChangedFor(nameof(TranslationKeyStatus))]
    private string _translationModel = "default";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TtsProviders))]
    [NotifyPropertyChangedFor(nameof(AvailableTtsOptions))]
    [NotifyPropertyChangedFor(nameof(SelectedTtsOption))]
    [NotifyPropertyChangedFor(nameof(TtsKeyStatus))]
    private ComputeProfile _ttsRuntime = ComputeProfile.Cloud;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableTtsOptions))]
    [NotifyPropertyChangedFor(nameof(SelectedTtsOption))]
    [NotifyPropertyChangedFor(nameof(TtsKeyStatus))]
    [NotifyPropertyChangedFor(nameof(IsTtsCloningProvider))]
    private string _ttsProvider = ProviderNames.EdgeTts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTtsOption))]
    [NotifyPropertyChangedFor(nameof(TtsKeyStatus))]
    private string _ttsModelOrVoice = "en-US-AriaNeural";

    // ── Multi-speaker routing controls ───────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiSpeakerNoSpeakersYet))]
    private bool _isMultiSpeakerEnabled;

    [ObservableProperty]
    private IReadOnlyList<string> _diarizationProviderOptions = [string.Empty];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunDiarizationOnly))]
    private string _diarizationProvider = string.Empty;

    [ObservableProperty]
    private decimal? _diarizationMinSpeakers;

    [ObservableProperty]
    private decimal? _diarizationMaxSpeakers;

    private string _autoSpeakerDetectionStatus = "Manual speaker mapping is the default release flow.";

    public string AutoSpeakerDetectionStatus
    {
        get => _autoSpeakerDetectionStatus;
        private set
        {
            if (!SetProperty(ref _autoSpeakerDetectionStatus, value))
                return;

            OnPropertyChanged(nameof(HasAutoSpeakerDetectionStatus));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpeakers))]
    [NotifyPropertyChangedFor(nameof(IsMultiSpeakerNoSpeakersYet))]
    private ObservableCollection<string> _speakerIds = [];

    [ObservableProperty]
    private string? _selectedSpeakerId;

    [ObservableProperty]
    private string _selectedSpeakerAssignedVoice = "";

    [ObservableProperty]
    private string _selectedSpeakerReferenceAudioPath = "";

    private double _preMuteVolume = 1.0;
    private bool _isDucked;
    private bool _preFullscreenSegmentPaneVisible = true;
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

    private DispatcherTimer? _positionTimer;
    private readonly DispatcherTimer _controlsHideTimer;
    private const int ControlsHideDelayMs = 3000;
    private const double PositionUpdateThresholdMs = 0.5;

    /// <summary>
    /// Initializes a new EmbeddedPlaybackViewModel and wires up provider/state caches, timers, and coordinator event handlers.
    /// </summary>
    /// <param name="coordinator">The session workflow coordinator that provides session state, registries, and pipeline operations.</param>
    /// <param name="apiKeyStore">Optional store for API keys used by providers; may be null when not required.</param>
    /// <param name="errorDialogService">Optional service to show error dialogs; may be null in non-UI or test contexts.</param>
    /// <param name="logFilePath">Optional path for diagnostic log output; may be null to disable file logging.</param>
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
        Pipeline = new EmbeddedPlaybackPipelineViewModel(this, coordinator);
        SpeakerRouting = new EmbeddedPlaybackSpeakerRoutingViewModel(this, coordinator);
        BuildProviderCaches();
        RebuildDiarizationProviderOptions();
        SyncProviderModelFieldsFromSettings();

        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
        _coordinator.SettingsModified += OnCoordinatorSettingsModified;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += OnPositionTimerTick;
        _positionTimer.Start();

        _controlsHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ControlsHideDelayMs) };
        _controlsHideTimer.Tick += OnControlsHideTimerTick;
    }

    public SessionWorkflowCoordinator Coordinator => _coordinator;
    public EmbeddedPlaybackPipelineViewModel Pipeline { get; }
    public EmbeddedPlaybackSpeakerRoutingViewModel SpeakerRouting { get; }

    public PlaybackState PlaybackState => _coordinator.PlaybackState;

    public string? ActiveTtsSegmentId => _coordinator.ActiveTtsSegmentId;

    public static IReadOnlyList<string> PlaybackRateOptions { get; } =
        ["0.25x", "0.5x", "0.75x", "1x", "1.25x", "1.5x", "2x"];

    public static IReadOnlyList<ComputeProfile> InferenceRuntimeOptions { get; } =
        [ComputeProfile.Cpu, ComputeProfile.Gpu, ComputeProfile.Cloud];

    public string SegmentPaneToggleLabel => IsSegmentPaneVisible ? "\u25C4" : "\u25BA";

    // Visible always in windowed mode; follows controls auto-hide in fullscreen
    public bool IsPullTabVisible => !IsFullscreen || IsControlsVisible;

    // Pull tabs appear only when panes are collapsed and the chrome is visible.
    public bool IsPanePullTabVisible => !IsSegmentPaneVisible && IsPullTabVisible;

    public string PlayPauseSourceLabel => IsSourcePaused ? "\u25B6\uFE0E" : "\u23F8\uFE0E";

    public string VolumeIconLabel => IsMuted || SourceVolume == 0
        ? "\U0001F507"
        : SourceVolume < 0.10
            ? "\U0001F508"
            : SourceVolume < 0.51
                ? "\U0001F509"
                : "\U0001F50A";

    public static string DubModeLabel => "🎙 Dub";

    public string SubtitleToggleLabel => IsSubtitleModeOn ? "CC ✓" : "CC";

    public string SpeechRateLabel => $"{SpeechRate:F1}x";
    public string AudioDuckingLabel => $"{AudioDuckingDb:F1} dB";
    public bool CanRunPipeline => !IsBusy;
    public bool CanRunDiarizationOnly =>
        !IsBusy &&
        _coordinator.CurrentSession.Stage >= SessionWorkflowStage.Transcribed &&
        !string.IsNullOrWhiteSpace(DiarizationProvider);
    public bool HasErrorDetails => !string.IsNullOrWhiteSpace(StatusErrorDetail);
    public bool HasDiagnosticsWarning => !_coordinator.BootstrapDiagnostics.AllDependenciesAvailable;
    public string DiagnosticsWarningText => _coordinator.BootstrapDiagnostics.DiagnosticSummary;
    public bool HasVsrPlaybackStatus => _coordinator.VideoEnhancementDiagnostics.HasPlaybackStatus;
    public string VsrPlaybackStatusText => _coordinator.VideoEnhancementDiagnostics.PlaybackStatusText;
    public string VoiceModelLabel => _coordinator.CurrentSession.TtsVoice ?? _coordinator.CurrentSettings.TtsVoice;
    // ── Language routing ──────────────────────────────────────────────────────

    /// <summary>Supported target languages. Expand as TTS voice coverage grows.</summary>
    public static IReadOnlyList<(string Code, string Label)> SupportedTargetLanguages { get; } =
    [
        ("en", "English"),
    ];

    public IReadOnlyList<string> TargetLanguageLabels { get; } =
        [.. SupportedTargetLanguages.Select(l => l.Label)];

    [ObservableProperty]
    private string _selectedTargetLanguage = "English";

    partial void OnSelectedTargetLanguageChanged(string value)
    {
        if (_isSynchronizingPipelineSettings) return;
        var entry = SupportedTargetLanguages.FirstOrDefault(l => l.Label == value);
        var code = string.IsNullOrEmpty(entry.Code) ? "en" : entry.Code;
        if (_coordinator.CurrentSettings.TargetLanguage != code)
        {
            _coordinator.CurrentSettings.TargetLanguage = code;
            _coordinator.NotifySettingsModified();
            NotifyActiveConfigChanged();
        }
    }

    /// <summary>Source language display: "auto-detect" before transcription has run.</summary>
    public string SourceLanguageDisplay =>
        string.IsNullOrEmpty(_coordinator.CurrentSession.SourceLanguage)
            ? "auto-detect"
            : _coordinator.CurrentSession.SourceLanguage;

    public string ActiveTranscriptionConfigLine => $"{TranscriptionRuntime} / {TranscriptionProvider} / {TranscriptionModel}";
    public string ActiveCpuTuningLine
    {
        get
        {
            var s = _coordinator.CurrentSettings;
            var threads = s.TranscriptionCpuThreads > 0 ? s.TranscriptionCpuThreads.ToString() : "auto";
            return $"{s.TranscriptionCpuComputeType} · threads {threads} · workers {Math.Max(1, s.TranscriptionNumWorkers)}";
        }
    }
    public string ActiveTranslationConfigLine
    {
        get
        {
            var fallback = _coordinator.TranslationFallbackNote;
            var target = _coordinator.CurrentSettings.TargetLanguage;
            if (fallback is not null)
                return $"{TranslationRuntime} / ⚠ {fallback} · target {target}";
            return $"{TranslationRuntime} / {TranslationProvider} / {TranslationModel} · target {target}";
        }
    }
    public string ActiveTtsConfigLine => $"{TtsRuntime} / {TtsProvider} / {TtsModelOrVoice}";
    public string SelectedSegmentSpeakerId => SelectedSegment?.SpeakerId ?? "—";
    public string SelectedSegmentAssignedVoice => SelectedSegment?.AssignedVoice ?? "—";
    public string SelectedSegmentReferenceStatus => SelectedSegment?.HasReferenceAudio == true ? "Yes" : "No";
    public bool HasSpeakers => SpeakerIds.Count > 0;
    public bool HasAutoSpeakerDetectionStatus => !string.IsNullOrWhiteSpace(AutoSpeakerDetectionStatus);

    /// <summary>True when multi-speaker mode is on but no speaker IDs have been detected yet.</summary>
    public bool IsMultiSpeakerNoSpeakersYet => IsMultiSpeakerEnabled && !HasSpeakers;

    /// <summary>True when the active TTS provider uses voice cloning from reference audio (Qwen).</summary>
    public bool IsTtsCloningProvider =>
        string.Equals(TtsProvider, ProviderNames.Qwen, StringComparison.Ordinal);

    [ObservableProperty]
    private string _defaultTtsVoiceFallback = string.Empty;

    // ── Provider / model option lists ──────────────────────────────────────────
    public IReadOnlyList<string> TranscriptionProviders => GetTranscriptionProviderIds(TranscriptionRuntime);

    public IReadOnlyList<string> TranslationProviders => GetTranslationProviderIds(TranslationRuntime);

    public IReadOnlyList<string> TtsProviders => GetTtsProviderIds(TtsRuntime);

    public IReadOnlyList<ModelOptionViewModel> AvailableTranscriptionModels => _availableTranscriptionModels;

    public IReadOnlyList<ModelOptionViewModel> AvailableTranslationModels => _availableTranslationModels;

    public IReadOnlyList<ModelOptionViewModel> AvailableTtsOptions => _availableTtsOptions;

    // ── Pipeline Progress ──────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PipelineProgressStatusLine))]
    private double _pipelineProgressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PipelineProgressStatusLine))]
    private bool _isPipelineProgressVisible;

    [ObservableProperty]
    private string _pipelineStageTitle = string.Empty;

    [ObservableProperty]
    private string _pipelineStageDetail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PipelineProgressStatusLine))]
    private bool _isPipelineProgressIndeterminate;

    public string PipelineProgressStatusLine =>
        !IsPipelineProgressVisible
            ? string.Empty
            : IsPipelineProgressIndeterminate
                ? "Current stage progress is active, but this provider has not reported a numeric percentage yet."
                : $"Current stage progress: {PipelineProgressPercent:P0}. The bar resets for each remaining pipeline stage.";

    // ── API key / readiness status for UI lock indicators ──────────────────────
    public string TranscriptionKeyStatus => _transcriptionKeyStatus;

    public string TranslationKeyStatus => _translationKeyStatus;

    public string TtsKeyStatus => _ttsKeyStatus;
    public ObservableCollection<ProviderHealthSnapshot> ProviderHealthSnapshots => _providerHealthSnapshots;

    internal void ApplyPipelineStageUpdate(SessionWorkflowCoordinator.PipelineStageUpdate update) =>
        Pipeline.ApplyStageUpdate(update);

    internal void ShowPipelineRefreshDetail(string detail) =>
        Pipeline.ShowRefreshDetail(detail);

    internal void ResetPipelineProgressState() =>
        Pipeline.ResetProgressState();

    internal sealed record ProviderDiagnosticsSelectionSnapshot(
        ComputeProfile TranscriptionRuntime,
        string TranscriptionProvider,
        string TranscriptionModel,
        ComputeProfile TranslationRuntime,
        string TranslationProvider,
        string TranslationModel,
        ComputeProfile TtsRuntime,
        string TtsProvider,
        string TtsModelOrVoice,
        string DiarizationProvider,
        string GpuServiceUrl);

    private static string GetReadinessStatus(ProviderReadiness readiness)
    {
        if (readiness.IsReady)
            return string.Empty;

        if (readiness.RequiresModelDownload)
            return "⬇ Download required (will run automatically)";

        if (readiness.BlockingReason?.Contains(
                // Message format produced by ContainerizedProviderReadiness.MapProbeResultToReadiness:
                // "{hostLabel} is starting at {serviceUrl}..."
                " is starting at ",
                StringComparison.Ordinal) == true)
        {
            return $"⏳ {readiness.BlockingReason}";
        }

        return $"⚠️ {readiness.BlockingReason}";
    }

    private void RefreshProviderHealthDiagnostics(bool force = false)
    {
        var snapshot = CaptureProviderHealthSelectionSnapshot();
        QueueProviderHealthRefresh(snapshot, force);
    }

    internal ProviderDiagnosticsSelectionSnapshot CaptureProviderHealthSelectionSnapshot() =>
        new(
            TranscriptionRuntime,
            TranscriptionProvider,
            TranscriptionModel,
            TranslationRuntime,
            TranslationProvider,
            TranslationModel,
            TtsRuntime,
            TtsProvider,
            TtsModelOrVoice,
            DiarizationProvider,
            _coordinator.CurrentSettings.EffectiveContainerizedServiceUrl);

    private void QueueProviderHealthRefresh(ProviderDiagnosticsSelectionSnapshot snapshot, bool force = false)
    {
        if (!force && snapshot == _lastQueuedProviderHealthSnapshot)
            return;

        _lastQueuedProviderHealthSnapshot = snapshot;
        var version = Interlocked.Increment(ref _providerHealthRefreshVersion);
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _providerHealthRefreshCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        _coordinator.Log.Info(
            $"Provider diagnostics refresh queued: v={version}, " +
            $"selection=({snapshot.TranscriptionRuntime}/{snapshot.TranscriptionProvider}/{snapshot.TranscriptionModel}, " +
            $"{snapshot.TranslationRuntime}/{snapshot.TranslationProvider}/{snapshot.TranslationModel}, " +
            $"{snapshot.TtsRuntime}/{snapshot.TtsProvider}/{snapshot.TtsModelOrVoice}, " +
            $"{snapshot.DiarizationProvider}), " +
            $"gpuServiceUrl={snapshot.GpuServiceUrl}");

        _ = RefreshProviderHealthDiagnosticsAsync(snapshot, version, cts.Token);
    }

    private async Task RefreshProviderHealthDiagnosticsAsync(
        ProviderDiagnosticsSelectionSnapshot snapshot,
        int version,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);

            var health = await ComputeProviderHealthSnapshotsAsync(snapshot, cancellationToken);
            await ApplyProviderHealthSnapshotsAsync(health, version, cancellationToken);

            if (UsesContainerizedRuntime(snapshot)
                && ContainsStartingStatus(health)
                && _coordinator.ContainerizedProbe is not null)
            {
                _ = await _coordinator.ContainerizedProbe.WaitForProbeAsync(
                    snapshot.GpuServiceUrl,
                    forceRefresh: false,
                    waitTimeout: TimeSpan.FromSeconds(30),
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                var settledHealth = await ComputeProviderHealthSnapshotsAsync(snapshot, cancellationToken);
                await ApplyProviderHealthSnapshotsAsync(settledHealth, version, cancellationToken);
            }

            stopwatch.Stop();
            _coordinator.Log.Info(
                $"Provider diagnostics refresh complete: v={version}, elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _coordinator.Log.Info(
                $"Provider diagnostics refresh canceled: v={version}, elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _coordinator.Log.Error(
                $"Provider diagnostics refresh failed: v={version}, elapsedMs={stopwatch.ElapsedMilliseconds}",
                ex);
        }
    }

    private Task<IReadOnlyList<ProviderHealthSnapshot>> ComputeProviderHealthSnapshotsAsync(
        ProviderDiagnosticsSelectionSnapshot snapshot,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var transcription = BuildTranscriptionHealthSnapshot(snapshot);
            var translation = BuildTranslationHealthSnapshot(snapshot);
            var tts = BuildTtsHealthSnapshot(snapshot);
            var diarization = BuildDiarizationHealthSnapshot(snapshot);
            return (IReadOnlyList<ProviderHealthSnapshot>)[transcription, translation, tts, diarization];
        }, cancellationToken);

    private async Task ApplyProviderHealthSnapshotsAsync(
        IReadOnlyList<ProviderHealthSnapshot> health,
        int version,
        CancellationToken cancellationToken)
    {
        void Apply()
        {
            if (version != _providerHealthRefreshVersion || cancellationToken.IsCancellationRequested)
                return;

            _providerHealthSnapshots.Clear();
            foreach (var snapshot in health)
                _providerHealthSnapshots.Add(snapshot);

            var transcription = health.FirstOrDefault(h => string.Equals(h.Section, "Transcription", StringComparison.Ordinal));
            var translation = health.FirstOrDefault(h => string.Equals(h.Section, "Translation", StringComparison.Ordinal));
            var tts = health.FirstOrDefault(h => string.Equals(h.Section, "TTS", StringComparison.Ordinal));
            var diarization = health.FirstOrDefault(h => string.Equals(h.Section, "Diarization", StringComparison.Ordinal));

            ApplyReadinessStatus(ref _transcriptionKeyStatus, transcription?.InlineStatus ?? string.Empty, nameof(TranscriptionKeyStatus));
            ApplyReadinessStatus(ref _translationKeyStatus, translation?.InlineStatus ?? string.Empty, nameof(TranslationKeyStatus));
            ApplyReadinessStatus(ref _ttsKeyStatus, tts?.InlineStatus ?? string.Empty, nameof(TtsKeyStatus));
            AutoSpeakerDetectionStatus = diarization?.InlineStatus ?? "Manual speaker mapping is the default release flow.";
            OnPropertyChanged(nameof(HasAutoSpeakerDetectionStatus));
        }

        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            // Running in a headless/test context with no Avalonia event loop, or already
            // on the UI thread — apply directly so the update is never lost.
            Apply();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(Apply);
        }
    }

    private static string GetRuntimeHostLabel(ComputeProfile runtime) => runtime switch
    {
        ComputeProfile.Gpu   => "Managed local GPU host",
        ComputeProfile.Cpu   => "Managed local CPU runtime",
        ComputeProfile.Cloud => "Cloud service",
        _                    => "Managed local CPU runtime",
    };

    internal ProviderHealthSnapshot BuildTranscriptionHealthSnapshot(ProviderDiagnosticsSelectionSnapshot snapshot) =>
        BuildHealthSnapshot(
            section: "Transcription",
            providerId: snapshot.TranscriptionProvider,
            selectionLabel: $"{snapshot.TranscriptionRuntime} / {snapshot.TranscriptionProvider} / {snapshot.TranscriptionModel}",
            runtimeLabel: snapshot.TranscriptionRuntime.ToString(),
            isContainerized: snapshot.TranscriptionRuntime == ComputeProfile.Gpu,
            gpuServiceUrl: snapshot.GpuServiceUrl,
            readinessFactory: () => _coordinator.TranscriptionRegistry.CheckReadiness(
                snapshot.TranscriptionProvider,
                snapshot.TranscriptionModel,
                _coordinator.CurrentSettings,
                _apiKeyStore,
                snapshot.TranscriptionRuntime),
            statusLineFactory: readiness => readiness.IsReady ? "Ready" : GetReadinessStatus(readiness),
            inlineStatusFactory: GetReadinessStatus,
            hostLabel: GetRuntimeHostLabel(snapshot.TranscriptionRuntime));

    internal ProviderHealthSnapshot BuildTranslationHealthSnapshot(ProviderDiagnosticsSelectionSnapshot snapshot) =>
        BuildHealthSnapshot(
            section: "Translation",
            providerId: snapshot.TranslationProvider,
            selectionLabel: $"{snapshot.TranslationRuntime} / {snapshot.TranslationProvider} / {snapshot.TranslationModel}",
            runtimeLabel: snapshot.TranslationRuntime.ToString(),
            isContainerized: snapshot.TranslationRuntime == ComputeProfile.Gpu,
            gpuServiceUrl: snapshot.GpuServiceUrl,
            readinessFactory: () => _coordinator.TranslationRegistry.CheckReadiness(
                snapshot.TranslationProvider,
                snapshot.TranslationModel,
                _coordinator.CurrentSettings,
                _apiKeyStore,
                snapshot.TranslationRuntime),
            statusLineFactory: readiness => readiness.IsReady ? "Ready" : GetReadinessStatus(readiness),
            inlineStatusFactory: GetReadinessStatus,
            hostLabel: GetRuntimeHostLabel(snapshot.TranslationRuntime));

    internal ProviderHealthSnapshot BuildTtsHealthSnapshot(ProviderDiagnosticsSelectionSnapshot snapshot) =>
        BuildHealthSnapshot(
            section: "TTS",
            providerId: snapshot.TtsProvider,
            selectionLabel: $"{snapshot.TtsRuntime} / {snapshot.TtsProvider} / {snapshot.TtsModelOrVoice}",
            runtimeLabel: snapshot.TtsRuntime.ToString(),
            isContainerized: snapshot.TtsRuntime == ComputeProfile.Gpu,
            gpuServiceUrl: snapshot.GpuServiceUrl,
            readinessFactory: () => _coordinator.TtsRegistry.CheckReadiness(
                snapshot.TtsProvider,
                snapshot.TtsModelOrVoice,
                _coordinator.CurrentSettings,
                _apiKeyStore,
                snapshot.TtsRuntime),
            statusLineFactory: readiness => readiness.IsReady ? "Ready" : GetReadinessStatus(readiness),
            inlineStatusFactory: GetReadinessStatus,
            hostLabel: GetRuntimeHostLabel(snapshot.TtsRuntime));

    internal ProviderHealthSnapshot BuildDiarizationHealthSnapshot(ProviderDiagnosticsSelectionSnapshot snapshot)
    {
        var registry = _coordinator.DiarizationRegistry;
        if (registry is null)
            return BuildManualDiarizationSnapshot("⚠ Speaker diarization is unavailable in this build. Manual mapping remains available.");

        if (string.IsNullOrWhiteSpace(snapshot.DiarizationProvider))
            return BuildManualDiarizationSnapshot("Manual speaker mapping is the default release flow.");

        var provider = registry
            .GetAvailableProviders()
            .FirstOrDefault(desc => string.Equals(desc.Id, snapshot.DiarizationProvider, StringComparison.Ordinal));

        if (provider is null)
            return BuildManualDiarizationSnapshot($"⚠ Unknown diarization provider '{snapshot.DiarizationProvider}'. Manual mapping will still work.");

        var isContainerized = provider.EffectiveDefaultRuntime == InferenceRuntime.Containerized;
        var hostLabel = isContainerized ? "Managed local GPU host" : "Managed local CPU runtime";
        return BuildHealthSnapshot(
            section: "Diarization",
            providerId: provider.Id,
            selectionLabel: $"{provider.EffectiveDefaultRuntime} / {provider.DisplayName}",
            runtimeLabel: provider.EffectiveDefaultRuntime.ToString(),
            isContainerized: isContainerized,
            gpuServiceUrl: snapshot.GpuServiceUrl,
            readinessFactory: () => registry.CheckReadiness(provider.Id, _coordinator.CurrentSettings, _apiKeyStore),
            statusLineFactory: readiness => readiness.IsReady ? "Ready" : GetReadinessStatus(readiness),
            inlineStatusFactory: readiness =>
                readiness.IsReady
                    ? $"Speaker diarization is enabled via {provider.DisplayName}."
                    : $"⚠ {provider.DisplayName} is not ready: {readiness.BlockingReason}. Manual mapping will still work.",
            hostLabel: hostLabel);
    }

    private ProviderHealthSnapshot BuildManualDiarizationSnapshot(string inlineStatus) =>
        new(
            "Diarization",
            ProviderNames.Manual,
            "Manual speaker mapping",
            "Local",
            "Not configured",
            NormalizeDiagnosticText(inlineStatus),
            "No diarization provider selected.",
            "No diarization provider selected.",
            string.Empty,
            IsReady: false,
            IsLive: false,
            IsStale: false,
            CheckedAtText: DateTimeOffset.UtcNow.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
            History: AppendProviderHealthHistory(
                $"Diarization|{ProviderNames.Manual}|Manual speaker mapping|Local",
                DateTimeOffset.UtcNow,
                "Not configured",
                "No diarization provider selected.",
                isReady: false));

    private ProviderHealthSnapshot BuildHealthSnapshot(
        string section,
        string providerId,
        string selectionLabel,
        string runtimeLabel,
        bool isContainerized,
        string? gpuServiceUrl,
        Func<ProviderReadiness> readinessFactory,
        Func<ProviderReadiness, string> statusLineFactory,
        Func<ProviderReadiness, string> inlineStatusFactory,
        string hostLabel)
    {
        ProviderReadiness readiness;
        try
        {
            readiness = readinessFactory();
        }
        catch (Exception ex)
        {
            var checkedAtUtc = DateTimeOffset.UtcNow;
            var hostStateText = isContainerized
                ? $"{hostLabel} unavailable"
                : $"{hostLabel} ({runtimeLabel})";
            var statusLineText = $"⚠ {section} readiness check failed";
            var inlineStatusText = $"⚠ {section} readiness check failed: {ex.Message}";
            var historyEntries = AppendProviderHealthHistory(
                $"{section}|{providerId}|{selectionLabel}|{runtimeLabel}",
                checkedAtUtc,
                statusLineText,
                hostStateText,
                isReady: false);

            return new ProviderHealthSnapshot(
                section,
                providerId,
                selectionLabel,
                runtimeLabel,
                statusLineText,
                inlineStatusText,
                ex.Message,
                hostStateText,
                string.Empty,
                IsReady: false,
                IsLive: false,
                IsStale: false,
                CheckedAtText: checkedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                History: historyEntries);
        }

        ContainerizedProbeResult? probeResult = null;
        if (isContainerized && _coordinator.ContainerizedProbe is not null && !string.IsNullOrWhiteSpace(gpuServiceUrl))
        {
            probeResult = _coordinator.ContainerizedProbe.GetCurrentOrStartBackgroundProbe(gpuServiceUrl);
        }

        var remoteProviderHealth = ResolveRemoteProviderHealth(probeResult, section, providerId);
        var checkedAt = DateTimeOffset.UtcNow;
        var statusLine = NormalizeDiagnosticText(statusLineFactory(readiness));
        var inlineStatus = NormalizeDiagnosticText(inlineStatusFactory(readiness));
        var detail = string.IsNullOrWhiteSpace(remoteProviderHealth?.Detail)
            ? readiness.RequiresModelDownload
            ? readiness.ModelDownloadDescription ?? readiness.BlockingReason ?? "Model download required."
            : readiness.BlockingReason ?? (readiness.IsReady ? "Ready" : "Not ready")
            : remoteProviderHealth!.Detail!;
        var hostState = BuildHostStateText(hostLabel, runtimeLabel, probeResult, isContainerized);
        var metricsText = BuildMetricsText(section, providerId, probeResult, remoteProviderHealth);
        var history = remoteProviderHealth is { History.Count: > 0 }
            ? remoteProviderHealth.History.Select(FormatProviderHistoryEntry).ToArray()
            : AppendProviderHealthHistory(
                $"{section}|{providerId}|{selectionLabel}|{runtimeLabel}",
                checkedAt,
                statusLine,
                hostState,
                readiness.IsReady);
        var checkedAtText = TryFormatCheckedAt(remoteProviderHealth?.CheckedAt)
            ?? checkedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        return new ProviderHealthSnapshot(
            section,
            providerId,
            selectionLabel,
            runtimeLabel,
            statusLine,
            inlineStatus,
            detail,
            hostState,
            metricsText,
            IsReady: readiness.IsReady,
            IsLive: isContainerized ? probeResult?.State == ContainerizedProbeState.Available : readiness.IsReady,
            IsStale: probeResult?.IsStale == true || remoteProviderHealth?.IsStale == true,
            CheckedAtText: checkedAtText,
            History: history);
    }

    private static string NormalizeDiagnosticText(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? text
            : text
                .Replace("âš  ", "Warning: ", StringComparison.Ordinal)
                .Replace("âœ“", "ok", StringComparison.Ordinal)
                .Replace("Â·", "|", StringComparison.Ordinal);

    private static string BuildHostStateText(
        string hostLabel,
        string runtimeLabel,
        ContainerizedProbeResult? probeResult,
        bool isContainerized)
    {
        if (!isContainerized)
            return $"{hostLabel} ({runtimeLabel})";

        if (probeResult is null)
            return $"{hostLabel} checking";

        return probeResult.State switch
        {
            ContainerizedProbeState.Checking => $"{hostLabel} checking",
            ContainerizedProbeState.Unavailable => $"{hostLabel} unavailable",
            ContainerizedProbeState.Available when probeResult.IsStale => $"{hostLabel} live (stale)",
            ContainerizedProbeState.Available => $"{hostLabel} live",
            _ => $"{hostLabel} checking",
        };
    }

    private static ContainerProviderHealthSnapshot? ResolveRemoteProviderHealth(
        ContainerizedProbeResult? probeResult,
        string section,
        string providerId)
    {
        if (probeResult is null || string.IsNullOrWhiteSpace(providerId))
            return null;

        if (string.Equals(section, "TTS", StringComparison.Ordinal))
        {
            if (probeResult.Capabilities?.TryGetTtsProviderHealth(providerId, out var ttsHealth) == true)
                return ttsHealth;

            if (probeResult.ProviderHealth is not null && probeResult.ProviderHealth.TryGetValue(providerId, out var liveTtsHealth))
                return liveTtsHealth;
        }

        if (string.Equals(section, "Diarization", StringComparison.Ordinal))
        {
            if (probeResult.Capabilities?.TryGetDiarizationProviderHealth(providerId, out var diarizationHealth) == true)
                return diarizationHealth;

            if (probeResult.ProviderHealth is not null && probeResult.ProviderHealth.TryGetValue(providerId, out var liveDiarizationHealth))
                return liveDiarizationHealth;
        }

        return null;
    }

    private static string BuildMetricsText(
        string section,
        string providerId,
        ContainerizedProbeResult? probeResult,
        ContainerProviderHealthSnapshot? remoteProviderHealth)
    {
        if (!string.Equals(section, "TTS", StringComparison.Ordinal)
            || !string.Equals(providerId, ProviderNames.Qwen, StringComparison.Ordinal)
            || probeResult is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (probeResult.QwenMaxConcurrency > 0)
            parts.Add($"Qwen concurrency {probeResult.QwenMaxConcurrency}");
        if (probeResult.QwenQueueDepth > 0)
            parts.Add($"queue {probeResult.QwenQueueDepth}");
        if (probeResult.ActiveQwenRequests > 0)
            parts.Add($"active {probeResult.ActiveQwenRequests}");
        if (probeResult.QwenLastQueueWaitMs.HasValue)
            parts.Add($"last wait {probeResult.QwenLastQueueWaitMs.Value:F0} ms");
        if (probeResult.QwenLastReferencePrepMs.HasValue)
            parts.Add($"ref {probeResult.QwenLastReferencePrepMs.Value:F0} ms");
        if (probeResult.QwenLastGenerationMs.HasValue)
            parts.Add($"gen {probeResult.QwenLastGenerationMs.Value:F0} ms");
        if (probeResult.QwenLastWarmupMs.HasValue)
            parts.Add($"warmup {probeResult.QwenLastWarmupMs.Value:F0} ms");

        if (parts.Count == 0 && remoteProviderHealth?.Metrics is { Count: > 0 })
        {
            foreach (var metric in remoteProviderHealth.Metrics)
                parts.Add($"{metric.Key}={metric.Value}");
        }

        return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
    }

    private static string FormatProviderHistoryEntry(ContainerProviderHealthHistoryEntry entry)
    {
        var timestamp = TryFormatCheckedAt(entry.Timestamp) ?? "unknown";
        var state = entry.Ready ? "ready" : "not ready";
        var detail = string.IsNullOrWhiteSpace(entry.Detail) ? string.Empty : $" · {entry.Detail}";
        var category = string.IsNullOrWhiteSpace(entry.FailureCategory) ? string.Empty : $" · {entry.FailureCategory}";
        return $"{timestamp} · {state}{detail}{category}";
    }

    private static string? TryFormatCheckedAt(string? isoTimestamp)
    {
        if (string.IsNullOrWhiteSpace(isoTimestamp))
            return null;

        if (!DateTimeOffset.TryParse(isoTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return isoTimestamp;

        return parsed.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    }

    private IReadOnlyList<string> AppendProviderHealthHistory(
        string key,
        DateTimeOffset checkedAtUtc,
        string statusLine,
        string hostState,
        bool isReady)
    {
        var entry = $"{checkedAtUtc.ToLocalTime():HH:mm:ss} · {(isReady ? "ready" : "not ready")} · {statusLine}{(string.IsNullOrWhiteSpace(hostState) ? string.Empty : $" · {hostState}")}";
        lock (_providerHealthHistoryLock)
        {
            if (!_providerHealthHistoryByKey.TryGetValue(key, out var queue))
            {
                queue = new Queue<string>();
                _providerHealthHistoryByKey[key] = queue;
            }

            if (queue.Count >= 3)
                queue.Dequeue();
            queue.Enqueue(entry);
            return queue.ToArray();
        }
    }

    private bool UsesContainerizedRuntime(ProviderDiagnosticsSelectionSnapshot snapshot) =>
        snapshot.TranscriptionRuntime == ComputeProfile.Gpu
        || snapshot.TranslationRuntime == ComputeProfile.Gpu
        || snapshot.TtsRuntime == ComputeProfile.Gpu
        || IsContainerizedDiarizationProvider(snapshot.DiarizationProvider);

    private bool IsContainerizedDiarizationProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || _coordinator.DiarizationRegistry is null)
            return false;

        var provider = _coordinator.DiarizationRegistry
            .GetAvailableProviders()
            .FirstOrDefault(desc => string.Equals(desc.Id, providerId, StringComparison.Ordinal));
        return provider?.EffectiveDefaultRuntime == InferenceRuntime.Containerized;
    }

    private static bool ContainsStartingStatus(IReadOnlyList<ProviderHealthSnapshot> health) =>
        health.Any(snapshot => IsStartingStatus(snapshot.StatusLine));

    private static bool IsStartingStatus(string status) =>
        status.StartsWith('⏳');

    internal string ResolveDiarizationProviderLabel()
    {
        if (string.IsNullOrWhiteSpace(DiarizationProvider))
            return "speaker";

        var registry = _coordinator.DiarizationRegistry;
        return registry?
            .GetAvailableProviders()
            .FirstOrDefault(provider => string.Equals(provider.Id, DiarizationProvider, StringComparison.Ordinal))
            ?.DisplayName
            ?? DiarizationProvider;
    }

    private void ApplyReadinessStatus(ref string field, string value, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
            return;

        field = value;
        OnPropertyChanged(propertyName);
    }

    internal void ClearStatusErrorDetail()
    {
        StatusErrorTitle = null;
        StatusErrorDetail = null;
    }

    internal void SetStatusErrorDetail(string title, Exception ex)
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
    if (IsDubModeOn && !IsSourcePaused)
        SyncDubToCurrentPosition(seekVideoToSegmentStart: true);
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

    /// <summary>
    /// Handles changes to the multi-speaker enabled setting and updates coordinator state and UI data.
    /// </summary>
    /// <param name="value">`true` to enable multi-speaker mode; `false` to disable. When `false`, clears the selected diarization provider.</param>
    partial void OnIsMultiSpeakerEnabledChanged(bool value)
    {
        if (_isSynchronizingPipelineSettings) return;

        if (!value)
            DiarizationProvider = string.Empty;

        _coordinator.SetMultiSpeakerEnabled(value);
        _ = RefreshSegmentsAsync();
    }

    /// <summary>
    /// Updates the availability of the diarization-only command when the view model's busy state changes.
    /// </summary>
    /// <param name="value">The new busy state; true when the view model is busy, false otherwise.</param>
    partial void OnIsBusyChanged(bool value)
    {
        RunDiarizationOnlyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Handle a change to the diarization provider selection by normalizing the selection, applying it to current settings, and updating related UI state and availability.
    /// </summary>
    /// <param name="value">The newly selected diarization provider identifier or display value.</param>
    partial void OnDiarizationProviderChanged(string value)
    {
        if (_isSynchronizingPipelineSettings) return;

        var normalized = NormalizeDiarizationProviderSelection(value);

        _isSynchronizingPipelineSettings = true;
        try
        {
            if (!string.Equals(normalized, value, StringComparison.Ordinal))
                DiarizationProvider = normalized;
        }
        finally
        {
            _isSynchronizingPipelineSettings = false;
        }

        _coordinator.CurrentSettings.DiarizationProvider = normalized;
        _coordinator.NotifySettingsModified();
        RefreshProviderHealthDiagnostics();
        RunDiarizationOnlyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Handle changes to the minimum diarization speaker count by updating the coordinator's current settings and signaling that settings were modified.
    /// </summary>
    /// <param name="value">The new minimum speaker count; nullable to clear. The value is normalized to a valid integer speaker count before being applied.</param>
    partial void OnDiarizationMinSpeakersChanged(decimal? value)
    {
        if (_isSynchronizingPipelineSettings) return;

        var normalized = NormalizeSpeakerCount(value);
        if (HasInvalidDiarizationSpeakerBounds(normalized, _coordinator.CurrentSettings.DiarizationMaxSpeakers))
        {
            RejectInvalidDiarizationSpeakerBoundsChange(
                nameof(DiarizationMinSpeakers),
                _coordinator.CurrentSettings.DiarizationMinSpeakers);
            return;
        }

        _coordinator.CurrentSettings.DiarizationMinSpeakers = normalized;
        _coordinator.NotifySettingsModified();
    }

    partial void OnDiarizationMaxSpeakersChanged(decimal? value)
    {
        if (_isSynchronizingPipelineSettings) return;

        var normalized = NormalizeSpeakerCount(value);
        if (HasInvalidDiarizationSpeakerBounds(_coordinator.CurrentSettings.DiarizationMinSpeakers, normalized))
        {
            RejectInvalidDiarizationSpeakerBoundsChange(
                nameof(DiarizationMaxSpeakers),
                _coordinator.CurrentSettings.DiarizationMaxSpeakers);
            return;
        }

        _coordinator.CurrentSettings.DiarizationMaxSpeakers = normalized;
        _coordinator.NotifySettingsModified();
    }

    partial void OnDefaultTtsVoiceFallbackChanged(string value)
    {
        if (_isSynchronizingPipelineSettings) return;

        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var normalizedDisplayValue = normalized ?? string.Empty;

        if (!string.Equals(value, normalizedDisplayValue, StringComparison.Ordinal))
        {
            _isSynchronizingPipelineSettings = true;
            try
            {
            DefaultTtsVoiceFallback = normalizedDisplayValue;
            }
            finally
            {
            _isSynchronizingPipelineSettings = false;
            }
        }

        _coordinator.SetDefaultTtsVoiceFallback(normalized);
    }

    private static int? NormalizeSpeakerCount(decimal? value)
    {
        if (!value.HasValue)
            return null;

        var rounded = (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, 1, 20);
    }

    private static bool HasInvalidDiarizationSpeakerBounds(int? minSpeakers, int? maxSpeakers) =>
        minSpeakers.HasValue && maxSpeakers.HasValue && minSpeakers.Value > maxSpeakers.Value;

    private void RejectInvalidDiarizationSpeakerBoundsChange(string propertyName, int? previousValue)
    {
        _isSynchronizingPipelineSettings = true;
        try
        {
            var previousDecimal = previousValue.HasValue ? (decimal?)previousValue.Value : null;
            if (string.Equals(propertyName, nameof(DiarizationMinSpeakers), StringComparison.Ordinal))
                DiarizationMinSpeakers = previousDecimal;
            else
                DiarizationMaxSpeakers = previousDecimal;
        }
        finally
        {
            _isSynchronizingPipelineSettings = false;
        }

        StatusText = "Diarization min speakers cannot be greater than max speakers.";
        ClearStatusErrorDetail();
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

        // Also resume when the player reached EOF naturally — IsSourcePaused is not set in that case.
        if (IsSourcePaused || player.HasEnded)
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
        // Only apply dub when source is not paused to prevent TTS playing while video is paused
        if (IsDubModeOn && !IsSourcePaused)
            ApplyDubForSegment(segment);
    }

    partial void OnSourceVolumeChanged(double value)
    {
        if (IsMuted && value > 0) IsMuted = false;
        RecalculateOutputVolumes();
    }

    partial void OnAudioDuckingDbChanged(double value)
    {
        RecalculateOutputVolumes();
    }

    partial void OnIsMutedChanged(bool value)
    {
        RecalculateOutputVolumes();
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
        _coordinator.TtsPlaybackRate = value;
    }

    partial void OnTranscriptionRuntimeChanged(ComputeProfile value)
    {
        if (_isSynchronizingPipelineSettings)
            return;

        var provider = ResolveTranscriptionProviderForRuntime(value, TranscriptionProvider);
        var model = ResolveTranscriptionModelId(value, provider, TranscriptionModel);

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            transcriptionRuntime: value,
            transcriptionProvider: provider,
            transcriptionModel: model));
    }

    partial void OnTranscriptionProviderChanged(string value)
    {
        if (_isSynchronizingPipelineSettings || string.IsNullOrEmpty(value)) return;

        var model = ResolveTranscriptionModelId(TranscriptionRuntime, value, TranscriptionModel);
        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            transcriptionProvider: value,
            transcriptionModel: model));
    }

    partial void OnTranscriptionModelChanged(string value)
    {
        if (_isSynchronizingPipelineSettings || string.IsNullOrEmpty(value)) return;

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            transcriptionModel: value));
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

    partial void OnTranslationRuntimeChanged(ComputeProfile value)
    {
        if (_isSynchronizingPipelineSettings)
            return;

        var provider = ResolveTranslationProviderForRuntime(value, TranslationProvider);
        var model = ResolveTranslationModelId(value, provider, TranslationModel);

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            translationRuntime: value,
            translationProvider: provider,
            translationModel: model));
    }

    partial void OnTranslationProviderChanged(string value)
    {
        if (_isSynchronizingPipelineSettings || string.IsNullOrEmpty(value)) return;

        var model = ResolveTranslationModelId(TranslationRuntime, value, TranslationModel);
        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            translationProvider: value,
            translationModel: model));
    }

    partial void OnTranslationModelChanged(string value)
    {
        if (_isSynchronizingPipelineSettings || string.IsNullOrEmpty(value)) return;

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            translationModel: value));
    }

    partial void OnTtsRuntimeChanged(ComputeProfile value)
    {
        if (_isSynchronizingPipelineSettings)
            return;

        var provider = ResolveTtsProviderForRuntime(value, TtsProvider);
        var model = ResolveTtsModelId(value, provider, TtsModelOrVoice);

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            ttsRuntime: value,
            ttsProvider: provider,
            ttsVoice: model));
    }

    partial void OnTtsProviderChanged(string value)
    {
        if (_isSynchronizingPipelineSettings || string.IsNullOrEmpty(value)) return;

        var model = ResolveTtsModelId(TtsRuntime, value, TtsModelOrVoice);
        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            ttsProvider: value,
            ttsVoice: model));
    }

    partial void OnTtsModelOrVoiceChanged(string value)
    {
        if (_isSynchronizingPipelineSettings || string.IsNullOrEmpty(value)) return;

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            ttsVoice: value));
    }

    private void OnCoordinatorSettingsModified()
    {
        SyncProviderModelFieldsFromSettings();
        NotifyActiveConfigChanged();
        OnPropertyChanged(nameof(VoiceModelLabel));
    }

    /// <summary>
    /// Synchronizes the view-model's provider, runtime, and model selection fields from the coordinator's current settings.
    /// Called when CurrentSession changes (e.g., media restored from cache) to ensure dropdowns always display
    /// the actual configured state, not stale values from a previous session.
    /// </summary>
    /// <remarks>
    /// Updates TTS playback rate, resolves and selects runtimes/providers/models, rebuilds model option lists,
    /// ensures multi-speaker mode is enabled, refreshes diarization provider options and related settings,
    /// and triggers provider readiness, auto-speaker detection, and speaker-id list refreshes.
    /// </remarks>
    private void SyncProviderModelFieldsFromSettings()
    {
        _isSynchronizingPipelineSettings = true;
        try
        {
            SpeechRate = _coordinator.TtsPlaybackRate;

            TranscriptionRuntime = _coordinator.CurrentSettings.TranscriptionProfile;
            TranslationRuntime = _coordinator.CurrentSettings.TranslationProfile;
            TtsRuntime = _coordinator.CurrentSettings.TtsProfile;

            TranscriptionProvider = ResolveTranscriptionProviderForRuntime(
                TranscriptionRuntime,
                _coordinator.CurrentSettings.TranscriptionProvider);
            TranslationProvider = ResolveTranslationProviderForRuntime(
                TranslationRuntime,
                _coordinator.CurrentSettings.TranslationProvider);
            TtsProvider = ResolveTtsProviderForRuntime(
                TtsRuntime,
                _coordinator.CurrentSettings.TtsProvider);

            TranscriptionModel = ResolveTranscriptionModelId(
                TranscriptionRuntime,
                TranscriptionProvider,
                _coordinator.CurrentSettings.TranscriptionModel);
            TranslationModel = ResolveTranslationModelId(
                TranslationRuntime,
                TranslationProvider,
                _coordinator.CurrentSettings.TranslationModel);
            TtsModelOrVoice = ResolveTtsModelId(
                TtsRuntime,
                TtsProvider,
                _coordinator.CurrentSettings.TtsVoice);

            RebuildAllModelOptions();

            SelectedTranscriptionModel =
                _availableTranscriptionModels.FirstOrDefault(m => m.ModelId == TranscriptionModel)
                ?? (_availableTranscriptionModels.Count > 0 ? _availableTranscriptionModels[0] : null);
            SelectedTranslationModel =
                _availableTranslationModels.FirstOrDefault(m => m.ModelId == TranslationModel)
                ?? (_availableTranslationModels.Count > 0 ? _availableTranslationModels[0] : null);
            SelectedTtsOption =
                _availableTtsOptions.FirstOrDefault(m => m.ModelId == TtsModelOrVoice)
                ?? (_availableTtsOptions.Count > 0 ? _availableTtsOptions[0] : null);

            // Multi-speaker routing is always on; migrate old sessions that had it off.
            if (!_coordinator.CurrentSession.MultiSpeakerEnabled)
                _coordinator.SetMultiSpeakerEnabled(true);
            IsMultiSpeakerEnabled = true;

            // Target language dropdown
            SelectedTargetLanguage = SupportedTargetLanguages.FirstOrDefault(l => l.Code == _coordinator.CurrentSettings.TargetLanguage).Label ?? "English";

            RebuildDiarizationProviderOptions();
            DiarizationProvider = NormalizeDiarizationProviderSelection(_coordinator.CurrentSettings.DiarizationProvider);
            DiarizationMinSpeakers = _coordinator.CurrentSettings.DiarizationMinSpeakers;
            DiarizationMaxSpeakers = _coordinator.CurrentSettings.DiarizationMaxSpeakers;
            DefaultTtsVoiceFallback = _coordinator.CurrentSession.DefaultTtsVoiceFallback ?? string.Empty;

            // Notify after RebuildAllModelOptions() so bindings read the rebuilt backing fields.
            OnPropertyChanged(nameof(AvailableTranscriptionModels));
            OnPropertyChanged(nameof(AvailableTranslationModels));
            OnPropertyChanged(nameof(AvailableTtsOptions));
            RefreshProviderHealthDiagnostics();
            RebuildSpeakerIds();
        }
        finally
        {
            _isSynchronizingPipelineSettings = false;
        }
    }

    /// <summary>
    /// Rebuilds the list of available diarization provider identifiers for the UI selector.
    /// </summary>
    /// <remarks>
    /// The list always starts with an empty entry. When a coordinator diarization registry is available,
    /// each implemented provider's Id is appended (duplicates and empty Ids are ignored).
    /// The resulting collection is assigned to <see cref="DiarizationProviderOptions"/>.
    /// </remarks>
    private void RebuildDiarizationProviderOptions()
    {
        var options = new List<string> { string.Empty };

        if (_coordinator.DiarizationRegistry is not null)
        {
            foreach (var providerId in _coordinator.DiarizationRegistry
                         .GetAvailableProviders()
                         .Where(provider => provider.IsImplemented)
                         .Select(provider => provider.Id))
            {
                if (!string.IsNullOrWhiteSpace(providerId) &&
                    !options.Contains(providerId, StringComparer.Ordinal))
                {
                    options.Add(providerId);
                }
            }
        }

        DiarizationProviderOptions = options;
    }
    private string NormalizeDiarizationProviderSelection(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();

        return DiarizationProviderOptions.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : string.Empty;
    }

    /// <summary>
    /// Raises property-change notifications for the active transcription, CPU tuning, translation, and TTS configuration display lines.
    /// </summary>
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
            [.. _coordinator.TranscriptionRegistry
                .GetAvailableModels(TranscriptionProvider, TranscriptionRuntime, _coordinator.CurrentSettings)
                .Select(m => new ModelOptionViewModel(m, null, GetTranscriptionModelAvailability(TranscriptionProvider, m)))];
    }

    private void RebuildTranslationModelOptions()
    {
        _availableTranslationModels =
            [.. _coordinator.TranslationRegistry
                .GetAvailableModels(TranslationProvider, TranslationRuntime, _coordinator.CurrentSettings)
                .Select(m => new ModelOptionViewModel(m, null, GetTranslationModelAvailability(TranslationProvider, m)))];
    }

    private void RebuildTtsModelOptions()
    {
        _availableTtsOptions =
            [.. _coordinator.TtsRegistry
                .GetAvailableModels(TtsProvider, TtsRuntime, _coordinator.CurrentSettings)
                .Select(m => new ModelOptionViewModel(m, (TtsProvider == ProviderNames.Qwen && m.StartsWith("Qwen/")) ? m[5..] : null, GetTtsModelAvailability(TtsProvider, m)))];
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
            ProviderNames.CTranslate2 => ModelDownloader.IsCTranslate2TranslationModelDownloaded(model),
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

    internal void ResetInteractiveModes()
    {
        if (IsSubtitleModeOn)
            IsSubtitleModeOn = false;
        if (IsDubModeOn)
            IsDubModeOn = false;
    }

    private void BuildProviderCaches()
    {
        BuildProviderCache(
            _transcriptionProvidersByRuntime,
            _transcriptionProviderIdsByRuntime,
            runtime => _coordinator.TranscriptionRegistry.GetAvailableProviders(runtime));

        BuildProviderCache(
            _translationProvidersByRuntime,
            _translationProviderIdsByRuntime,
            runtime => _coordinator.TranslationRegistry.GetAvailableProviders(runtime));

        BuildProviderCache(
            _ttsProvidersByRuntime,
            _ttsProviderIdsByRuntime,
            runtime => _coordinator.TtsRegistry.GetAvailableProviders(runtime));
    }

    private static void BuildProviderCache(
        Dictionary<ComputeProfile, IReadOnlyList<ProviderDescriptor>> descriptorCache,
        Dictionary<ComputeProfile, IReadOnlyList<string>> idCache,
        Func<ComputeProfile, IReadOnlyList<ProviderDescriptor>> providerFactory)
    {
        descriptorCache.Clear();
        idCache.Clear();

        foreach (var runtime in InferenceRuntimeOptions)
        {
            var providers = providerFactory(runtime)
                .Where(p => p.IsImplemented)
                .ToArray();
            descriptorCache[runtime] = providers;
            idCache[runtime] = [.. providers.Select(p => p.Id)];
        }
    }

    private IReadOnlyList<string> GetTranscriptionProviderIds(ComputeProfile runtime) =>
        _transcriptionProviderIdsByRuntime.TryGetValue(runtime, out var providers)
            ? providers
            : [];

    private IReadOnlyList<string> GetTranslationProviderIds(ComputeProfile runtime) =>
        _translationProviderIdsByRuntime.TryGetValue(runtime, out var providers)
            ? providers
            : [];

    private IReadOnlyList<string> GetTtsProviderIds(ComputeProfile runtime) =>
        _ttsProviderIdsByRuntime.TryGetValue(runtime, out var providers)
            ? providers
            : [];

    private IReadOnlyList<ProviderDescriptor> GetTranscriptionProviderDescriptors(ComputeProfile runtime) =>
        _transcriptionProvidersByRuntime.TryGetValue(runtime, out var providers)
            ? providers
            : [];

    private IReadOnlyList<ProviderDescriptor> GetTranslationProviderDescriptors(ComputeProfile runtime) =>
        _translationProvidersByRuntime.TryGetValue(runtime, out var providers)
            ? providers
            : [];

    private IReadOnlyList<ProviderDescriptor> GetTtsProviderDescriptors(ComputeProfile runtime) =>
        _ttsProvidersByRuntime.TryGetValue(runtime, out var providers)
            ? providers
            : [];

    private string ResolveTranscriptionProviderForRuntime(ComputeProfile runtime, string? providerId)
    {
        var providers = GetTranscriptionProviderDescriptors(runtime);
        var normalized = InferenceRuntimeCatalog.NormalizeTranscriptionProvider(runtime, providerId);
        return providers.Any(provider => provider.Id == normalized)
            ? normalized
            : (providers.Count > 0 ? providers[0].Id : normalized);
    }

    private string ResolveTranslationProviderForRuntime(ComputeProfile runtime, string? providerId)
    {
        var providers = GetTranslationProviderDescriptors(runtime);
        var normalized = InferenceRuntimeCatalog.NormalizeTranslationProvider(runtime, providerId);
        return providers.Any(provider => provider.Id == normalized)
            ? normalized
            : (providers.Count > 0 ? providers[0].Id : normalized);
    }

    private string ResolveTtsProviderForRuntime(ComputeProfile runtime, string? providerId)
    {
        var providers = GetTtsProviderDescriptors(runtime);
        var normalized = InferenceRuntimeCatalog.NormalizeTtsProvider(runtime, providerId);
        return providers.Any(provider => provider.Id == normalized)
            ? normalized
            : (providers.Count > 0 ? providers[0].Id : normalized);
    }

    private string ResolveTranscriptionModelId(
        ComputeProfile runtime,
        string providerId,
        string? preferredModel) =>
        ResolveModelId(
            _coordinator.TranscriptionRegistry.GetAvailableModels(providerId, runtime, _coordinator.CurrentSettings),
            preferredModel);

    private string ResolveTranslationModelId(
        ComputeProfile runtime,
        string providerId,
        string? preferredModel) =>
        ResolveModelId(
            _coordinator.TranslationRegistry.GetAvailableModels(providerId, runtime, _coordinator.CurrentSettings),
            preferredModel);

    private string ResolveTtsModelId(
        ComputeProfile runtime,
        string providerId,
        string? preferredModel) =>
        ResolveModelId(
            _coordinator.TtsRegistry.GetAvailableModels(providerId, runtime, _coordinator.CurrentSettings),
            preferredModel);

    private static string ResolveModelId(
        IReadOnlyList<string> supportedModels,
        string? preferredModel)
    {
        if (supportedModels.Count == 0)
            return "default";

        if (!string.IsNullOrWhiteSpace(preferredModel)
            && supportedModels.Contains(preferredModel, StringComparer.Ordinal))
        {
            return preferredModel;
        }

        return supportedModels[0];
    }

    private PipelineSettingsSelection CreatePipelineSettingsSelection(
        ComputeProfile? transcriptionRuntime = null,
        string? transcriptionProvider = null,
        string? transcriptionModel = null,
        ComputeProfile? translationRuntime = null,
        string? translationProvider = null,
        string? translationModel = null,
        ComputeProfile? ttsRuntime = null,
        string? ttsProvider = null,
        string? ttsVoice = null) =>
        new(
            transcriptionRuntime ?? TranscriptionRuntime,
            transcriptionProvider ?? TranscriptionProvider,
            transcriptionModel ?? TranscriptionModel,
            translationRuntime ?? TranslationRuntime,
            translationProvider ?? TranslationProvider,
            translationModel ?? TranslationModel,
            ttsRuntime ?? TtsRuntime,
            ttsProvider ?? TtsProvider,
            ttsVoice ?? TtsModelOrVoice,
            _coordinator.CurrentSettings.TargetLanguage);

    /// <summary>
    /// Single source of truth for all audio output levels.
    /// Call this whenever SourceVolume, IsMuted, AudioDuckingDb, or _isDucked changes.
    /// </summary>
    private void RecalculateOutputVolumes()
    {
        double masterGain = IsMuted ? 0.0 : SourceVolume;

        // Source audio: apply ducking attenuation when TTS is active
        double sourceGain = _isDucked
            ? masterGain * Math.Pow(10.0, AudioDuckingDb / 20.0)
            : masterGain;

        _coordinator.SourceMediaPlayer?.Volume = sourceGain;

        // TTS audio: push master gain through coordinator so it re-applies after every Load()
        _coordinator.TtsVolume = masterGain;
    }

    private void ApplyDucking()
    {
        if (_isDucked) return;
        _isDucked = true;
        RecalculateOutputVolumes();
    }

    private void RestoreDucking()
    {
        if (!_isDucked) return;
        _isDucked = false;
        RecalculateOutputVolumes();
    }

    partial void OnIsDubModeOnChanged(bool value)
    {
        if (!value)
        {
            ApplyDubForSegment(null);
        }
        else if (!IsSourcePaused)
        {
            // Video is currently playing — seek to segment start and start TTS immediately.
            SyncDubToCurrentPosition(seekVideoToSegmentStart: true);
        }
        // If paused: no auto-play; the Play button applies the dub-aware path on next press.
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
            IsSegmentPaneVisible = _preFullscreenSegmentPaneVisible;
            _controlsHideTimer.Stop();
            IsControlsVisible = true;   // always visible outside fullscreen
        }
    }

    partial void OnIsSubtitleModeOnChanged(bool value)
    {
        ApplySubtitleState();
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

    internal void UpdateSelectedSpeakerDetails(string? speakerId)
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

    // Core dub-sync helper. Restores ducking, stops any in-flight TTS, updates
    // _lastDubbedSegment, and — when seg is non-null and seekVideoToSegmentStart is true —
    // seeks the video to seg's start. When seg has audio, applies ducking and starts TTS.
    // Pass null for seg to stop and clear only (on pause, manual scrub, or dub-mode-off).
    private void ApplyDubForSegment(WorkflowSegmentState? seg, bool seekVideoToSegmentStart = false)
    {
        RestoreDucking();
        _coordinator.StopTtsPlayback();
        _lastDubbedSegment = seg;
        if (seg == null) return;
        if (seekVideoToSegmentStart)
            _coordinator.SourceMediaPlayer?.Seek((long)(seg.StartSeconds * 1000));
        if (seg.HasTtsAudio)
        {
            ApplyDucking();
            _ = _coordinator.PlayTtsForSegmentAsync(seg.SegmentId);
        }
    }

    private void UpdateDubMode()
    {
        if (IsSourcePaused) return;
        var currentSeg = FindSegmentAt(SourcePositionMs / 1000.0);
        if (currentSeg?.SegmentId == _lastDubbedSegment?.SegmentId) return;
        ApplyDubForSegment(currentSeg);
    }

    // Finds the segment at the current playback position and delegates to ApplyDubForSegment.
    // Pass seekVideoToSegmentStart: true when resuming play (seek to segment start for clean A/V
    // alignment); false when the video position is already correct (e.g., timer-driven update).
    private void SyncDubToCurrentPosition(bool seekVideoToSegmentStart)
        => ApplyDubForSegment(FindSegmentAt(SourcePositionMs / 1000.0), seekVideoToSegmentStart);

    private async void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
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
            case nameof(SessionWorkflowCoordinator.RuntimeWarmupStatusText):
                StatusText = string.IsNullOrWhiteSpace(_coordinator.RuntimeWarmupStatusText)
                    ? _coordinator.CurrentSession.StatusMessage
                    : _coordinator.RuntimeWarmupStatusText;
                break;
            case nameof(SessionWorkflowCoordinator.VideoEnhancementDiagnostics):
                OnPropertyChanged(nameof(HasVsrPlaybackStatus));
                OnPropertyChanged(nameof(VsrPlaybackStatusText));
                break;
            case nameof(SessionWorkflowCoordinator.TranslationFallbackNote):
                NotifyActiveConfigChanged();
                break;
            case nameof(SessionWorkflowCoordinator.CurrentSession):
                OnPropertyChanged(nameof(VoiceModelLabel));
                OnPropertyChanged(nameof(SourceLanguageDisplay));
                OnPropertyChanged(nameof(CanRunDiarizationOnly));
                RunDiarizationOnlyCommand.NotifyCanExecuteChanged();
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
                    await RefreshSegmentsAsync();
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
                if (IsDubModeOn)
                    SyncDubToCurrentPosition(seekVideoToSegmentStart: true);

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
            if (IsDubModeOn)
                ApplyDubForSegment(null);
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
        IsSegmentPaneVisible = !IsSegmentPaneVisible;
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
    internal async Task RefreshSegmentsAsync(List<WorkflowSegmentState>? segments = null)
    {
        try
        {
            var list = segments ?? await _coordinator.GetSegmentWorkflowListAsync();
            // Guard: replacing the Segments collection can cause Avalonia's ListBox TwoWay binding
            // to update SelectedSegment (e.g., by re-confirming a structurally-equal item in the
            // new collection). Without this guard, OnSelectedSegmentChanged would call
            // SeekAndPlayAsync unexpectedly. Nulling SelectedSegment first ensures the ListBox
            // has no prior selection to re-confirm when ItemsSource changes.
            _isUpdatingActiveSegment = true;
            try
            {
                SelectedSegment = null;
                Segments = new ObservableCollection<WorkflowSegmentState>(list);
                HasSegments = Segments.Count > 0;
                StatusText = HasSegments
                    ? $"{Segments.Count} segments loaded."
                    : "No segments available. Run the workflow first.";
                ClearStatusErrorDetail();
                if (IsSubtitleModeOn) ApplySubtitleState();
            }
            finally
            {
                _isUpdatingActiveSegment = false;
            }
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

    [RelayCommand]
    private void CancelPipeline() => Pipeline.Cancel();

    [RelayCommand]
    private Task RunPipelineAsync() => Pipeline.RunAsync();

    [RelayCommand]
    private void ClearPipeline() => Pipeline.Clear();

    [RelayCommand(CanExecute = nameof(CanRunDiarizationOnly))]
    private Task RunDiarizationOnlyAsync() => Pipeline.RunDiarizationOnlyAsync();

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
    private Task AssignSelectedSpeakerVoiceAsync() => SpeakerRouting.AssignSelectedSpeakerVoiceAsync();

    [RelayCommand]
    private Task ClearSelectedSpeakerVoiceAsync() => SpeakerRouting.ClearSelectedSpeakerVoiceAsync();

    public Task SetReferenceAudioForSelectedSpeaker(string path) =>
        SpeakerRouting.SetReferenceAudioForSelectedSpeakerAsync(path);

    [RelayCommand]
    private Task ClearSelectedSpeakerReferenceAudioAsync() => SpeakerRouting.ClearSelectedSpeakerReferenceAudioAsync();

    public void Dispose()
    {
        _coordinator.PropertyChanged -= OnCoordinatorPropertyChanged;
        _coordinator.SettingsModified -= OnCoordinatorSettingsModified;
        Pipeline.Dispose();

        if (_positionTimer is not null)
        {
            _positionTimer.Stop();
            _positionTimer.Tick -= OnPositionTimerTick;
            _positionTimer = null;
        }

        _controlsHideTimer.Stop();
        _controlsHideTimer.Tick -= OnControlsHideTimerTick;

        _providerHealthRefreshCts?.Cancel();
        _providerHealthRefreshCts?.Dispose();
        _providerHealthRefreshCts = null;

        GC.SuppressFinalize(this);
    }
}

public sealed record ProviderHealthSnapshot(
    string Section,
    string ProviderId,
    string SelectionLabel,
    string RuntimeLabel,
    string StatusLine,
    string InlineStatus,
    string Detail,
    string HostState,
    string MetricsText,
    bool IsReady,
    bool IsLive,
    bool IsStale,
    string CheckedAtText,
    IReadOnlyList<string> History);
