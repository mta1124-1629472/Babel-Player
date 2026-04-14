using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
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
        string? logFilePath = null)
    {
        _coordinator = coordinator;
        _apiKeyStore = apiKeyStore;
        _errorDialogService = errorDialogService;
        _logFilePath = logFilePath;

        Preview = new EmbeddedPlaybackPreviewViewModel(this, coordinator);
        Pipeline = new EmbeddedPlaybackPipelineViewModel(this, coordinator);
        SpeakerRouting = new EmbeddedPlaybackSpeakerRoutingViewModel(this, coordinator);

        BuildProviderCaches();
        SyncProviderModelFieldsFromSettings();

        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
        _coordinator.SettingsModified += OnCoordinatorSettingsModified;
    }

    public SessionWorkflowCoordinator Coordinator => _coordinator;
    public EmbeddedPlaybackPreviewViewModel Preview { get; }
    public EmbeddedPlaybackPipelineViewModel Pipeline { get; }
    public EmbeddedPlaybackSpeakerRoutingViewModel SpeakerRouting { get; }

    public bool HasErrorDetails => !string.IsNullOrWhiteSpace(StatusErrorDetail);
    public bool HasDiagnosticsWarning => !_coordinator.BootstrapDiagnostics.AllDependenciesAvailable;
    public string DiagnosticsWarningText => _coordinator.BootstrapDiagnostics.DiagnosticSummary;
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

    partial void OnIsBusyChanged(bool value) => Pipeline.NotifyBusyStateChanged();

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
        _coordinator.PropertyChanged -= OnCoordinatorPropertyChanged;
        _coordinator.SettingsModified -= OnCoordinatorSettingsModified;

        Preview.Dispose();
        Pipeline.Dispose();
        DisposeProviderHealthDiagnostics();

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
