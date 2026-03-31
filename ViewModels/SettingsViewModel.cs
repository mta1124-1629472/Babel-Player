using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Babel.Player.Services;
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

        // Theme options
        ThemeOptions = new[] { "Light", "Dark", "System" };
        
        // TTS voice options
        TtsVoiceOptions = new[]
        {
            "en-US-AriaNeural",
            "en-US-JennyNeural",
            "en-US-GuyNeural",
            "es-ES-LauraNeural",
            "es-ES-PabloNeural",
            "fr-FR-DeniseNeural",
            "de-DE-KatjaNeural",
            "it-IT-ElsaNeural",
            "pt-BR-FranciscaNeural",
            "ja-JP-NanamiNeural",
            "ko-KR-SunHiNeural",
            "zh-CN-XiaoxiaoNeural",
            "zh-CN-YunxiNeural",
            "ar-SA-ZariyahNeural",
            "hi-IN-SwaraNeural",
            "ru-RU-SvetlanaNeural",
        };

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

    // Models tab
    public ModelsTabViewModel ModelsTab { get; }

    // Recent Sessions
    [ObservableProperty]
    private int _maxRecentSessions;

    // Auto-save
    [ObservableProperty]
    private bool _autoSaveEnabled;

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
        
        settings.TtsVoice = SelectedVoice ?? settings.TtsVoice;
        settings.Theme = SelectedTheme ?? settings.Theme;
        settings.MaxRecentSessions = MaxRecentSessions;
        settings.AutoSaveEnabled = AutoSaveEnabled;

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