using System;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Settings;
using CommunityToolkit.Mvvm.Input;
using SettingsService = Babel.Player.Services.Settings.SettingsService;

namespace Babel.Player.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;

    public MainWindowViewModel(SessionWorkflowCoordinator coordinator, SettingsService settingsService)
    {
        Coordinator = coordinator;
        _settingsService = settingsService;
        Playback = new EmbeddedPlaybackViewModel(coordinator);
        Inspection = new SegmentInspectionViewModel(Playback);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
    }


    public SessionWorkflowCoordinator Coordinator { get; }

    public IRelayCommand OpenSettingsCommand { get; }

    private void OpenSettings()
    {
        var settingsWindow = new Views.SettingsWindow();
        settingsWindow.DataContext = new SettingsViewModel(_settingsService, Coordinator, settingsWindow);
        settingsWindow.Show();
    }

    public EmbeddedPlaybackViewModel Playback { get; }

    public SegmentInspectionViewModel Inspection { get; }

    /// <summary>Returns the settings service and current settings for constructing a <see cref="SettingsViewModel"/>.</summary>
    public (SettingsService Service, AppSettings Current) GetSettingsContext() =>
        (_settingsService, Coordinator.CurrentSettings);

    [RelayCommand]
    private void RestoreSession(RecentSessionEntry entry) =>
        Coordinator.RestoreSession(entry.SessionId);
}
