using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Settings;
using CommunityToolkit.Mvvm.Input;
using SettingsService = Babel.Player.Services.Settings.SettingsService;

namespace Babel.Player.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly ModelDownloader _modelDownloader;
    private readonly ApiKeyStore? _apiKeyStore;

    public MainWindowViewModel(
        SessionWorkflowCoordinator coordinator,
        SettingsService settingsService,
        ModelDownloader modelDownloader,
        ApiKeyStore? apiKeyStore = null,
        IErrorDialogService? errorDialogService = null,
        IPipelineRefreshDialogService? pipelineRefreshDialogService = null,
        string? logFilePath = null)
    {
        Coordinator = coordinator;
        _settingsService = settingsService;
        _modelDownloader = modelDownloader;
        _apiKeyStore = apiKeyStore;

        Playback = new EmbeddedPlaybackViewModel(
            coordinator,
            apiKeyStore,
            errorDialogService,
            pipelineRefreshDialogService,
            logFilePath);
        Inspection = new SegmentInspectionViewModel(Playback);

        // Persist settings whenever the left-panel dropdowns change them in-place
        Coordinator.SettingsModified += OnCoordinatorSettingsModified;
    }

    public SessionWorkflowCoordinator Coordinator { get; }

    /// <summary>Local model downloads (Faster Whisper, Piper voices in wizard, etc.).</summary>
    public ModelDownloader ModelDownloader => _modelDownloader;

    public EmbeddedPlaybackViewModel Playback { get; }

    public SegmentInspectionViewModel Inspection { get; }

    /// <summary>Returns the settings service and current settings for constructing a <see cref="SettingsViewModel"/>.</summary>
    public (SettingsService Service, AppSettings Current) GetSettingsContext() =>
        (_settingsService, Coordinator.CurrentSettings);

    public SettingsViewModel CreateSettingsViewModel(Avalonia.Controls.Window ownerWindow) =>
        new(
            _settingsService,
            Coordinator,
            ownerWindow,
            new ModelsTabViewModel(_modelDownloader, Coordinator),
            containerizedManager: Coordinator.ContainerizedInferenceManager,
            apiKeyStore: _apiKeyStore);

    /// <summary>One-time tip after install: managed GPU host warm-up duration.</summary>
    public async Task TryShowManagedBackendWarmupNoticeAsync(Window owner)
    {
        var settings = Coordinator.CurrentSettings;
        if (settings.ShownManagedBackendWarmupNotice)
            return;

        if (!settings.AlwaysStartLocalGpuRuntimeAtAppStart)
            return;

        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 14,
        };
        panel.Children.Add(new TextBlock
        {
            Text =
                "The first time after install, an update, or app startup, the local host may need about 30 to 60 seconds before it is ready. " +
                "Wait until the status shows Ready, then run the pipeline.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 440,
        });
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, MinWidth = 96 };
        panel.Children.Add(ok);
        var dialog = new Window
        {
            Title = "Local inference host",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ok.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner).ConfigureAwait(true);

        settings.ShownManagedBackendWarmupNotice = true;
        _settingsService.Save(settings);
    }

    [RelayCommand]
    private void RestoreSession(RecentSessionEntry entry) =>
        Coordinator.RestoreSession(entry.SessionId);

    public void Dispose()
    {
        Coordinator.SettingsModified -= OnCoordinatorSettingsModified;
        Inspection.Dispose();
        Playback.Dispose();
    }

    private void OnCoordinatorSettingsModified() =>
        _settingsService.Save(Coordinator.CurrentSettings);
}
