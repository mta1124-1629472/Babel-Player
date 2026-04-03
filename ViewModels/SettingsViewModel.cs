using System.Collections.Generic;
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;
using SettingsService = Babel.Player.Services.Settings.SettingsService;
using Babel.Player.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly SessionWorkflowCoordinator _coordinator;
    private readonly Window _ownerWindow;

    public SettingsViewModel(
        SettingsService settingsService,
        SessionWorkflowCoordinator coordinator,
        Window ownerWindow,
        ModelsTabViewModel modelsTab)
    {
        _settingsService = settingsService;
        _coordinator = coordinator;
        _ownerWindow = ownerWindow;
        ModelsTab = modelsTab;

        // Load current settings from coordinator (not disk, to avoid losing side-panel changes)
        var current = _coordinator.CurrentSettings;
        SelectedVoice = current.TtsVoice;
        SelectedTheme = current.Theme;
        MaxRecentSessions = current.MaxRecentSessions;
        AutoSaveEnabled = current.AutoSaveEnabled;
        PreferredLocalGpuBackend = current.PreferredLocalGpuBackend;
        AdvancedGpuServiceUrl = current.AdvancedGpuServiceUrl;
        AlwaysStartLocalGpuRuntimeAtAppStart = current.AlwaysStartLocalGpuRuntimeAtAppStart;
        TranscriptionCpuComputeType = current.TranscriptionCpuComputeType;
        TranscriptionCpuThreads = current.TranscriptionCpuThreads;
        TranscriptionNumWorkers = current.TranscriptionNumWorkers;

        // Theme options
        ThemeOptions = new[] { "Light", "Dark", "System" };
        
        // TTS voice options
        TtsVoiceOptions = [.. TtsRegistry.EdgeTtsVoices];

        // Video hardware settings
        _videoHwdec         = current.VideoHwdec;
        _videoGpuApi        = current.VideoGpuApi;
        _videoExportEncoder = current.VideoExportEncoder;
        _videoUseGpuNext    = current.VideoUseGpuNext;

        // RTX Video Enhancement settings
        _videoVsrEnabled    = current.VideoVsrEnabled;
        _videoVsrQuality    = current.VideoVsrQuality;
        _videoHdrEnabled    = current.VideoHdrEnabled;
        _videoToneMapping   = current.VideoToneMapping;
        _videoTargetPeak    = current.VideoTargetPeak;
        _videoHdrComputePeak = current.VideoHdrComputePeak;

        // Hotkeys (default values)
        PlayPauseHotkey = "Space";
        ToggleSegmentPanelHotkey = "S";
        ToggleDubModeHotkey = "D";
        ToggleFullscreenHotkey = "F11";
    }

    // Theme
    [ObservableProperty]
    private string _selectedTheme = "Light";

    public string[] ThemeOptions { get; }

    // TTS Voice
    [ObservableProperty]
    private string _selectedVoice;

    public string[] TtsVoiceOptions { get; }

    public string[] TranscriptionCpuComputeTypeOptions { get; } =
        ["int8", "int8_float16", "float32"];

    // Models tab
    public ModelsTabViewModel ModelsTab { get; }

    // Recent Sessions
    [ObservableProperty]
    private int _maxRecentSessions;

    // Auto-save
    [ObservableProperty]
    private bool _autoSaveEnabled;

    // Containerized local inference
    [ObservableProperty]
    private GpuHostBackend _preferredLocalGpuBackend = GpuHostBackend.ManagedVenv;

    [ObservableProperty]
    private string _advancedGpuServiceUrl = "http://127.0.0.1:8000";

    [ObservableProperty]
    private bool _alwaysStartLocalGpuRuntimeAtAppStart;

    // Advanced transcription CPU tuning
    [ObservableProperty]
    private string _transcriptionCpuComputeType = "int8";

    [ObservableProperty]
    private int _transcriptionCpuThreads;

    [ObservableProperty]
    private int _transcriptionNumWorkers = 1;

    // Video hardware decode & encode
    [ObservableProperty]
    private string _videoHwdec = "auto";

    [ObservableProperty]
    private string _videoGpuApi = "auto";

    [ObservableProperty]
    private string _videoExportEncoder = "auto";

    [ObservableProperty]
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

    // RTX Video Enhancement settings
    [ObservableProperty]
    private bool _videoVsrEnabled;

    [ObservableProperty]
    private int _videoVsrQuality = 2;

    [ObservableProperty]
    private bool _videoHdrEnabled;

    [ObservableProperty]
    private string _videoToneMapping = "bt.2390";

    [ObservableProperty]
    private string _videoTargetPeak = "auto";

    [ObservableProperty]
    private bool _videoHdrComputePeak;

    public string[] ToneMappingOptions { get; } = ["bt.2390", "mobius", "clip", "auto"];

    // Hotkeys
    [ObservableProperty]
    private string _playPauseHotkey;

    [ObservableProperty]
    private string _toggleSegmentPanelHotkey;

    [ObservableProperty]
    private string _toggleDubModeHotkey;

    [ObservableProperty]
    private string _toggleFullscreenHotkey;

    [RelayCommand]
    private void Apply()
    {
        // Update the existing settings instance directly
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
        settings.VideoHwdec         = VideoHwdec;
        settings.VideoGpuApi        = VideoGpuApi;
        settings.VideoExportEncoder = VideoExportEncoder;
        settings.VideoUseGpuNext    = VideoUseGpuNext;
        settings.VideoVsrEnabled    = VideoVsrEnabled;
        settings.VideoVsrQuality    = VideoVsrQuality;
        settings.VideoHdrEnabled    = VideoHdrEnabled;
        settings.VideoToneMapping   = VideoToneMapping;
        settings.VideoTargetPeak    = VideoTargetPeak;
        settings.VideoHdrComputePeak = VideoHdrComputePeak;

        // Trigger persistence and notify listeners (like MainWindowViewModel) that settings have changed
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
}
