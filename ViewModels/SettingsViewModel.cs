using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SettingsService = Babel.Player.Services.Settings.SettingsService;

namespace Babel.Player.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly SessionWorkflowCoordinator _coordinator;
    private readonly ApiKeyStore? _apiKeyStore;
    private readonly Window _ownerWindow;
    private readonly IContainerizedInferenceManager _containerizedManager;
    private readonly Func<bool> _hdrDisplayStateProvider;
    private CancellationTokenSource? _restartCts;

    public SettingsViewModel(
        SettingsService settingsService,
        SessionWorkflowCoordinator coordinator,
        Window ownerWindow,
        ModelsTabViewModel modelsTab,
        IContainerizedInferenceManager? containerizedManager = null,
        ApiKeyStore? apiKeyStore = null,
        Func<bool>? hdrDisplayStateProvider = null)
    {
        _settingsService       = settingsService;
        _coordinator           = coordinator;
        _ownerWindow           = ownerWindow;
        ModelsTab              = modelsTab;
        _containerizedManager  = containerizedManager ?? NullInferenceManager.Instance;
        _apiKeyStore           = apiKeyStore;
        _hdrDisplayStateProvider = hdrDisplayStateProvider ?? HardwareSnapshot.QueryActiveHdrDisplay;

        var current = _coordinator.CurrentSettings;
        SelectedVoice          = current.TtsVoice;
        SelectedTheme          = current.Theme;
        MaxRecentSessions      = current.MaxRecentSessions;
        AutoSaveEnabled        = current.AutoSaveEnabled;
        PreferredLocalGpuBackend = current.PreferredLocalGpuBackend;
        AdvancedGpuServiceUrl  = current.AdvancedGpuServiceUrl;
        AlwaysStartLocalGpuRuntimeAtAppStart = current.AlwaysStartLocalGpuRuntimeAtAppStart;
        TranscriptionCpuComputeType = current.TranscriptionCpuComputeType;
        TranscriptionCpuThreads = current.TranscriptionCpuThreads;
        TranscriptionNumWorkers = current.TranscriptionNumWorkers;

        // Theme options
        ThemeOptions = ["Light", "Dark", "System"];

        // TTS voice options
        TtsVoiceOptions = [.. TtsRegistry.EdgeTtsVoices];

        // Video hardware settings
        _videoHwdec          = current.VideoHwdec;
        _videoGpuApi         = current.VideoGpuApi;
        _videoExportEncoder  = current.VideoExportEncoder;
        _videoUseGpuNext     = current.VideoUseGpuNext;

        // Video enhancement settings
        _videoVsrEnabled     = current.VideoVsrEnabled;
        _videoHdrEnabled     = current.VideoHdrEnabled;

        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;

        // Hotkeys (default values)
        PlayPauseHotkey             = "Space";
        ToggleSegmentPanelHotkey    = "S";
        ToggleDubModeHotkey         = "D";
        ToggleFullscreenHotkey      = "F11";
    }

    // ── About ─────────────────────────────────────────────────────────────────

    public static string AppVersion   => $"Version {BuildInfo.Version}";
    public static string AppBuildDate => $"Build date: {BuildInfo.BuildDate}";

    // ── Backend restart ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestartBackend))]
    private bool _isRestartingBackend;

    [ObservableProperty]
    private string _backendStatusText = "Idle";

    public bool CanRestartBackend => !IsRestartingBackend;

    [RelayCommand(CanExecute = nameof(CanRestartBackend))]
    private async Task RestartBackend()
    {
        _restartCts?.Cancel();
        _restartCts = new CancellationTokenSource();
        var ct = _restartCts.Token;

        IsRestartingBackend = true;
        BackendStatusText   = "Restarting\u2026";

        var settings = _coordinator.CurrentSettings;

        try
        {
            var result = await _containerizedManager
                .EnsureStartedAsync(settings, ContainerizedStartupTrigger.Manual, ct)
                .ConfigureAwait(true);

            BackendStatusText = result == ContainerizedStartResult.AlreadyRunning
                || result == ContainerizedStartResult.Started
                ? "Ready"
                : $"Status: {result}";
        }
        catch (OperationCanceledException)
        {
            BackendStatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            BackendStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsRestartingBackend = false;
        }
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _selectedTheme = "Light";

    partial void OnSelectedThemeChanged(string value)
    {
        // Apply theme change immediately when user selects from dropdown
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = value switch
            {
                "Dark" => ThemeVariant.Dark,
                "Light" => ThemeVariant.Light,
                _ => ThemeVariant.Default // System
            };
        }
    }

    public string[] ThemeOptions { get; }

    // ── TTS Voice ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _selectedVoice;

    public string[] TtsVoiceOptions { get; }

    public string[] TranscriptionCpuComputeTypeOptions { get; } =
        ["auto", "int8", "int8_float16", "float32"];

    // ── Models tab ────────────────────────────────────────────────────────────

    public ModelsTabViewModel ModelsTab { get; }

    // ── Recent Sessions ───────────────────────────────────────────────────────

    [ObservableProperty]
    private int _maxRecentSessions;

    // ── Auto-save ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _autoSaveEnabled;

    // ── Containerized local inference ─────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDockerBackend))]
    private GpuHostBackend _preferredLocalGpuBackend = GpuHostBackend.ManagedVenv;

    /// <summary>True when the Docker GPU backend is selected; controls whether the service URL field is editable.</summary>
    public bool IsDockerBackend => PreferredLocalGpuBackend == GpuHostBackend.DockerHost;

    [ObservableProperty]
    private string _advancedGpuServiceUrl = "http://127.0.0.1:8000";

    [ObservableProperty]
    private bool _alwaysStartLocalGpuRuntimeAtAppStart;

    // ── Advanced transcription CPU tuning ─────────────────────────────────────

    [ObservableProperty]
    private string _transcriptionCpuComputeType = "int8";

    [ObservableProperty]
    private int _transcriptionCpuThreads;

    [ObservableProperty]
    private int _transcriptionNumWorkers = 1;

    // ── Video hardware decode & encode ────────────────────────────────────────

    [ObservableProperty]
    private string _videoHwdec = "auto";

    [ObservableProperty]
    private string _videoGpuApi = "auto";

    [ObservableProperty]
    private string _videoExportEncoder = "auto";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HdrSettingsAvailable))]
    [NotifyPropertyChangedFor(nameof(HdrAvailabilityHintText))]
    [NotifyPropertyChangedFor(nameof(HasHdrAvailabilityHint))]
    private bool _videoUseGpuNext;

    public string[] HwdecOptions { get; } =
        ["auto", "auto-safe", "no", "d3d11va", "d3d11va-copy", "nvdec", "nvdec-copy", "qsv", "dxva2"];

    public string[] GpuApiOptions { get; } =
        ["auto", "d3d11", "vulkan", "opengl"];

    public GpuHostBackend[] GpuBackendOptions { get; } =
        [GpuHostBackend.ManagedVenv, GpuHostBackend.DockerHost];

    public string[] ExportEncoderOptions { get; } =
        ["auto", "h264_nvenc", "hevc_nvenc", "h264_amf", "hevc_amf",
         "h264_qsv", "hevc_qsv", "libx264", "libx265"];

    // ── Video enhancement settings ────────────────────────────────────────────

    [ObservableProperty]
    private bool _videoVsrEnabled;

    [ObservableProperty]
    private bool _videoHdrEnabled;

    public string VsrSupportHintText => _coordinator.VideoEnhancementDiagnostics.SupportHintText;
    public string VsrRequestedStateText => _coordinator.VideoEnhancementDiagnostics.RequestedStateText;
    public string VsrResolvedStateText => _coordinator.VideoEnhancementDiagnostics.ResolvedStateText;
    public string VsrReasonText => _coordinator.VideoEnhancementDiagnostics.LastReasonText;
    public string VsrFilterText => _coordinator.VideoEnhancementDiagnostics.LastFilterText;

    /// <summary>True when Windows HDR is currently active for at least one desktop output.</summary>
    public bool IsHdrDisplayActive => _hdrDisplayStateProvider();

    /// <summary>
    /// HDR passthrough requires both gpu-next and an active Windows HDR display pipeline.
    /// </summary>
    public bool HdrSettingsAvailable => VideoUseGpuNext && IsHdrDisplayActive;

    public string HdrAvailabilityHintText =>
        VideoUseGpuNext && !IsHdrDisplayActive
            ? "Enable HDR in Windows Display Settings to use HDR passthrough."
            : string.Empty;

    public bool HasHdrAvailabilityHint => !string.IsNullOrWhiteSpace(HdrAvailabilityHintText);

    public static string HdrDriverFeatureHintText =>
        "RTX Auto HDR (SDR→HDR) is a separate driver feature — enable it in NVIDIA Control Panel.";

    // ── Hotkeys ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _playPauseHotkey;

    [ObservableProperty]
    private string _toggleSegmentPanelHotkey;

    [ObservableProperty]
    private string _toggleDubModeHotkey;

    [ObservableProperty]
    private string _toggleFullscreenHotkey;

    // ── Apply / OK / Cancel ───────────────────────────────────────────────────

    [RelayCommand]
    private void Apply()
    {
        var settings = _coordinator.CurrentSettings;

        settings.TtsVoice           = SelectedVoice ?? settings.TtsVoice;
        settings.Theme              = SelectedTheme ?? settings.Theme;
        settings.MaxRecentSessions  = MaxRecentSessions;
        settings.AutoSaveEnabled    = AutoSaveEnabled;
        settings.PreferredLocalGpuBackend = PreferredLocalGpuBackend;
        settings.AdvancedGpuServiceUrl = string.IsNullOrWhiteSpace(AdvancedGpuServiceUrl)
            ? settings.AdvancedGpuServiceUrl
            : AdvancedGpuServiceUrl.Trim();
        settings.AlwaysStartLocalGpuRuntimeAtAppStart = AlwaysStartLocalGpuRuntimeAtAppStart;
        settings.TranscriptionCpuComputeType = string.IsNullOrWhiteSpace(TranscriptionCpuComputeType)
            ? "int8"
            : TranscriptionCpuComputeType;
        settings.TranscriptionCpuThreads = Math.Max(0, TranscriptionCpuThreads);
        settings.TranscriptionNumWorkers = Math.Max(1, TranscriptionNumWorkers);

        settings.VideoHwdec          = VideoHwdec;
        settings.VideoGpuApi         = VideoGpuApi;
        settings.VideoExportEncoder  = VideoExportEncoder;
        settings.VideoUseGpuNext     = VideoUseGpuNext;
        settings.VideoVsrEnabled     = VideoVsrEnabled;
        settings.VideoHdrEnabled     = VideoHdrEnabled;

        // Apply theme change immediately when Save & Close is pressed
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = SelectedTheme switch
            {
                "Dark" => ThemeVariant.Dark,
                "Light" => ThemeVariant.Light,
                _ => ThemeVariant.Default // System
            };
        }

        _coordinator.NotifySettingsModified();
    }

    [RelayCommand]
    private void OK()
    {
        Apply();
        _ownerWindow.Close();
    }

    [RelayCommand]
    private void Cancel()
    {
        _ownerWindow.Close();
    }

    [RelayCommand]
    private static void OpenKofi()
    {
        try
        {
            const string kofiUrl = "https://ko-fi.com/R5R01WOOYW";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = kofiUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open Ko-fi link: {ex.Message}");
        }
    }

    [RelayCommand]
    private static void OpenGitHubSponsors()
    {
        try
        {
            const string sponsorsUrl = "https://github.com/sponsors/mta1124-1629472";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = sponsorsUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open GitHub Sponsors link: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _coordinator.PropertyChanged -= OnCoordinatorPropertyChanged;
        _restartCts?.Cancel();
        _restartCts?.Dispose();
        _restartCts = null;
    }

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionWorkflowCoordinator.VideoEnhancementDiagnostics))
        {
            OnPropertyChanged(nameof(VsrSupportHintText));
            OnPropertyChanged(nameof(VsrRequestedStateText));
            OnPropertyChanged(nameof(VsrResolvedStateText));
            OnPropertyChanged(nameof(VsrReasonText));
            OnPropertyChanged(nameof(VsrFilterText));
        }

    }

    internal void RefreshHdrDisplayState()
    {
        OnPropertyChanged(nameof(IsHdrDisplayActive));
        OnPropertyChanged(nameof(HdrSettingsAvailable));
        OnPropertyChanged(nameof(HdrAvailabilityHintText));
        OnPropertyChanged(nameof(HasHdrAvailabilityHint));
    }

    // ── Null-object for tests / design-time ───────────────────────────────────

    private sealed class NullInferenceManager : IContainerizedInferenceManager
    {
        public static readonly NullInferenceManager Instance = new();
        public void RequestEnsureStarted(AppSettings s, ContainerizedStartupTrigger t) { }
        public Task<ContainerizedStartResult> EnsureStartedAsync(
            AppSettings s, ContainerizedStartupTrigger t, CancellationToken ct = default)
            => Task.FromResult(ContainerizedStartResult.AlreadyRunning);
    }
}
