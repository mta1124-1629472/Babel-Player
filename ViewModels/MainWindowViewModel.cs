using System;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Settings;
using CommunityToolkit.Mvvm.Input;
using SettingsService = Babel.Player.Services.Settings.SettingsService;

namespace Babel.Player.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly ApiKeyStore? _apiKeyStore;

    public MainWindowViewModel(
        SessionWorkflowCoordinator coordinator,
        SettingsService settingsService,
        ApiKeyStore? apiKeyStore = null)
    {
        Coordinator = coordinator;
        _settingsService = settingsService;
        _apiKeyStore = apiKeyStore;

        Playback = new EmbeddedPlaybackViewModel(coordinator, apiKeyStore);
        Inspection = new SegmentInspectionViewModel(Playback);


        // Persist settings whenever the left-panel dropdowns change them in-place
        Coordinator.SettingsModified += () => _settingsService.Save(Coordinator.CurrentSettings);
    }


    public SessionWorkflowCoordinator Coordinator { get; }



    public EmbeddedPlaybackViewModel Playback { get; }

    public SegmentInspectionViewModel Inspection { get; }

    /// <summary>Returns the settings service and current settings for constructing a <see cref="SettingsViewModel"/>.</summary>
    public (SettingsService Service, AppSettings Current) GetSettingsContext() =>
        (_settingsService, Coordinator.CurrentSettings);

    [RelayCommand]
    private void RestoreSession(RecentSessionEntry entry) =>
        Coordinator.RestoreSession(entry.SessionId);
}
