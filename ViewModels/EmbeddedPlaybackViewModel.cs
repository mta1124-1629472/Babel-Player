using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Reactive.Linq;
using Avalonia.Threading;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels;

public partial class EmbeddedPlaybackViewModel : ViewModelBase, IDisposable
{
    private readonly SessionWorkflowCoordinator _coordinator;
    private readonly ApiKeyStore? _apiKeyStore;
    private readonly IErrorDialogService? _errorDialogService;
    private readonly string? _logFilePath;
    private bool _isSynchronizingPipelineSettings;
    private readonly PropertyChangedEventHandler _previewPropertyChangedHandler;
    private IDisposable? _readinessSignalSubscription;

    [ObservableProperty]
    private string _statusText = "No segments loaded.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetails))]
    private string? _statusErrorTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetails))]
    private string? _statusErrorDetail;

    [ObservableProperty]
    private bool _isBusy;

    public EmbeddedPlaybackViewModel(
        SessionWorkflowCoordinator coordinator,
        ApiKeyStore? apiKeyStore = null,
        IErrorDialogService? errorDialogService = null,
        IPipelineRefreshDialogService? pipelineRefreshDialogService = null,
        string? logFilePath = null)
    {
        _coordinator = coordinator;
        _apiKeyStore = apiKeyStore;
        _errorDialogService = errorDialogService;
        _logFilePath = logFilePath;

        Preview = new EmbeddedPlaybackPreviewViewModel(this, coordinator);
        Pipeline = new EmbeddedPlaybackPipelineViewModel(this, coordinator, pipelineRefreshDialogService);
        SpeakerRouting = new EmbeddedPlaybackSpeakerRoutingViewModel(this, coordinator);

        BuildProviderCaches();
        SyncProviderModelFieldsFromSettings();

        _previewPropertyChangedHandler = OnPreviewPropertyChanged;
        Preview.PropertyChanged += _previewPropertyChangedHandler;
        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
        _coordinator.SettingsModified += OnCoordinatorSettingsModified;
        _readinessSignalSubscription = _coordinator.ReadinessSignals
            .Select(signal => (
                Signal: signal,
                Fingerprint: $"{signal.Kind}:{signal.Source}:{signal.Summary}:{signal.ForceRefresh}"))
            .DistinctUntilChanged(x => x.Fingerprint, StringComparer.Ordinal)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .Subscribe(payload => Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(DiagnosticsWarningText));
                RefreshProviderHealthDiagnostics(payload.Signal.ForceRefresh);
                NotifyVocalSeparationCapabilityProperties();
            }));
    }

    public SessionWorkflowCoordinator Coordinator => _coordinator;
    public EmbeddedPlaybackPreviewViewModel Preview { get; }
    public EmbeddedPlaybackPipelineViewModel Pipeline { get; }
    public EmbeddedPlaybackSpeakerRoutingViewModel SpeakerRouting { get; }

    public bool HasErrorDetails => !string.IsNullOrWhiteSpace(StatusErrorDetail);
    public bool HasDiagnosticsWarning => !_coordinator.BootstrapDiagnostics.AllDependenciesAvailable;
    public bool ShowDiagnosticsWarningBanner => HasDiagnosticsWarning && !Preview.IsFullscreen;
    public string DiagnosticsWarningText =>
        $"{_coordinator.BootstrapDiagnostics.DiagnosticSummary} · last update {FormatReadinessAge()}";
    public bool HasVsrPlaybackStatus => _coordinator.VideoEnhancementDiagnostics.HasPlaybackStatus;
    public string VsrPlaybackStatusText => _coordinator.VideoEnhancementDiagnostics.PlaybackStatusText;
    public PlaybackState PlaybackState => _coordinator.PlaybackState;
    public string? ActiveTtsSegmentId => _coordinator.ActiveTtsSegmentId;
    public string VoiceModelLabel => _coordinator.CurrentSession.TtsVoice ?? _coordinator.CurrentSettings.TtsVoice;
    public string SourceLanguageDisplay =>
        string.IsNullOrEmpty(_coordinator.CurrentSession.SourceLanguage)
            ? "auto-detect"
            : _coordinator.CurrentSession.SourceLanguage;
    public string HwCpuLine => _coordinator.HardwareSnapshot.CpuLine;
    public string HwGpuLine => _coordinator.HardwareSnapshot.GpuLine;
    public string HwRamLine => _coordinator.HardwareSnapshot.RamLine;
    public string HwNpuLine => _coordinator.HardwareSnapshot.NpuLine;
    public string HwLibsLine => _coordinator.HardwareSnapshot.LibsLine;
    public string HwInferenceLine => _coordinator.BootstrapDiagnostics.InferenceLine;

    internal bool IsSynchronizingPipelineSettings
    {
        get => _isSynchronizingPipelineSettings;
        set => _isSynchronizingPipelineSettings = value;
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

    internal void ResetInteractiveModes() => Preview.ResetInteractiveModes();

    partial void OnIsBusyChanged(bool value)
    {
        Pipeline.NotifyBusyStateChanged();
        Pipeline.NotifyPipelineFooterChrome();
    }

    private async void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => OnCoordinatorPropertyChangedCoreAsync(e.PropertyName));
            return;
        }

        await OnCoordinatorPropertyChangedCoreAsync(e.PropertyName);
    }

    private async Task OnCoordinatorPropertyChangedCoreAsync(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(SessionWorkflowCoordinator.PlaybackState):
                OnPropertyChanged(nameof(PlaybackState));
                break;
            case nameof(SessionWorkflowCoordinator.ActiveTtsSegmentId):
                OnPropertyChanged(nameof(ActiveTtsSegmentId));
                break;
            case nameof(SessionWorkflowCoordinator.BootstrapDiagnostics):
                OnPropertyChanged(nameof(HasDiagnosticsWarning));
                OnPropertyChanged(nameof(ShowDiagnosticsWarningBanner));
                OnPropertyChanged(nameof(DiagnosticsWarningText));
                OnPropertyChanged(nameof(HwInferenceLine));
                break;
            case nameof(SessionWorkflowCoordinator.ReadinessLastUpdatedUtc):
                OnPropertyChanged(nameof(DiagnosticsWarningText));
                break;
            case nameof(SessionWorkflowCoordinator.HardwareSnapshot):
                OnPropertyChanged(nameof(HwCpuLine));
                OnPropertyChanged(nameof(HwGpuLine));
                OnPropertyChanged(nameof(HwRamLine));
                OnPropertyChanged(nameof(HwNpuLine));
                OnPropertyChanged(nameof(HwLibsLine));
                RefreshRuntimeAvailabilityFromHardware();
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
                SyncProviderModelFieldsFromSettings();
                NotifyActiveConfigChanged();
                Pipeline.NotifySessionStateChanged();
                await Preview.HandleCurrentSessionChangedAsync();
                break;
        }
    }

    [RelayCommand]
    private async Task ShowStatusErrorDetailsAsync()
    {
        if (_errorDialogService is null || string.IsNullOrWhiteSpace(StatusErrorDetail))
            return;

        await _errorDialogService.ShowErrorAsync(
            StatusErrorTitle ?? "Error details",
            StatusErrorDetail,
            _logFilePath);
    }

    public void Dispose()
    {
        Preview.PropertyChanged -= _previewPropertyChangedHandler;
        _coordinator.PropertyChanged -= OnCoordinatorPropertyChanged;
        _coordinator.SettingsModified -= OnCoordinatorSettingsModified;
        _readinessSignalSubscription?.Dispose();
        _readinessSignalSubscription = null;

        Preview.Dispose();
        Pipeline.Dispose();
        DisposeProviderHealthDiagnostics();

        GC.SuppressFinalize(this);
    }

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EmbeddedPlaybackPreviewViewModel.IsFullscreen))
            OnPropertyChanged(nameof(ShowDiagnosticsWarningBanner));
    }

    private string FormatReadinessAge()
    {
        var timestamp = _coordinator.ReadinessLastUpdatedUtc;
        if (timestamp == DateTimeOffset.MinValue)
            return "pending";

        var age = DateTimeOffset.UtcNow - timestamp;
        if (age < TimeSpan.FromSeconds(1))
            return "just now";

        var seconds = Math.Max(1, (int)age.TotalSeconds);
        return $"{seconds.ToString(CultureInfo.CurrentCulture)}s ago";
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
