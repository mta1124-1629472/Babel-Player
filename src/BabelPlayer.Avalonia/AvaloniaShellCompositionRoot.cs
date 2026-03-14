using System.IO;
using Avalonia.Controls;
using BabelPlayer.App;
using BabelPlayer.Infrastructure;
using BabelPlayer.Playback.Mpv;

namespace BabelPlayer.Avalonia;

public sealed record AvaloniaShellDependencies(
    IPlaybackHostRuntime PlaybackHostRuntime,
    IShellPlaybackCommands ShellPlaybackCommands,
    IResumeDecisionCoordinator ResumeDecisionCoordinator,
    IShellPreferenceCommands ShellPreferenceCommands,
    IQueueCommands QueueCommands,
    IQueueProjectionReader QueueProjectionReader,
    IShellProjectionReader ShellProjectionReader,
    IShellPreferencesService ShellPreferencesService,
    IWindowModeService WindowModeService,
    IShellLibraryService ShellLibraryService,
    IShortcutProfileService ShortcutProfileService,
    IShortcutCommandExecutor ShortcutCommandExecutor,
    ResumePlaybackService ResumePlaybackService,
    ISubtitleWorkflowShellService SubtitleWorkflowService,
    ICredentialSetupService CredentialSetupService,
    IFilePickerService FilePickerService,
    IDisposable Lifetime) : IDisposable
{
    public void Dispose()
    {
        Lifetime.Dispose();
    }
}

public sealed class AvaloniaShellCompositionRoot
{
    public AvaloniaShellDependencies Create(Window ownerWindow)
    {
        var credentialFacade = new CredentialFacade();
        var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var workflowStateStore = new InMemorySubtitleWorkflowStateStore();
        var filePickerService = new AvaloniaFilePickerService(ownerWindow);
        var credentialDialogService = new AvaloniaCredentialDialogService(ownerWindow);
        var shellPreferencesService = CreateShellPreferencesService();
        var windowModeService = new AvaloniaWindowModeService(ownerWindow);
        var shortcutProfileService = new ShortcutProfileService(shellPreferencesService);
        var subtitleInfrastructure = new SubtitleWorkflowInfrastructureFactory().Create(new SubtitleWorkflowInfrastructureRequest(
            credentialFacade,
            credentialDialogService,
            filePickerService,
            Environment.GetEnvironmentVariable));
        var subtitleApplicationService = new SubtitleApplicationService(
            subtitleInfrastructure.SubtitleSourceResolver,
            subtitleInfrastructure.CaptionGenerator,
            subtitleInfrastructure.SubtitleTranslator,
            subtitleInfrastructure.AiCredentialCoordinator,
            subtitleInfrastructure.RuntimeProvisioner,
            credentialFacade,
            mediaSessionCoordinator,
            workflowStateStore,
            subtitleInfrastructure.ProviderAvailabilityService);
        var subtitleWorkflowController = new SubtitleWorkflowController(
            subtitleApplicationService,
            new SubtitleWorkflowProjectionAdapter(workflowStateStore, mediaSessionCoordinator.Store),
            new SubtitlePresentationProjector());
        var playbackBackend = new LibMpvPlaybackBackend();
        var playbackBackendCoordinator = new PlaybackBackendCoordinator(playbackBackend, mediaSessionCoordinator);
        var playbackHostRuntime = new PlaybackHostRuntimeAdapter(playbackBackend);
        var shellProjectionReader = new ShellProjectionService(mediaSessionCoordinator.Store);
        var playbackQueueController = new PlaybackQueueController();
        var resumePlaybackService = new ResumePlaybackService();
        var shellLibraryService = new ShellLibraryService(new LibraryBrowserService(), shellPreferencesService);
        var credentialSetupService = new CredentialSetupService(
            credentialFacade,
            subtitleInfrastructure.ProviderAvailabilityService,
            subtitleInfrastructure.AiCredentialCoordinator,
            subtitleInfrastructure.RuntimeProvisioner,
            Environment.GetEnvironmentVariable);
        var shellController = new ShellController(
            playbackQueueController,
            playbackBackend,
            subtitleWorkflowController,
            new LibraryBrowserService(),
            resumePlaybackService,
            shellPreferencesService);
        var resumeDecisionCoordinator = new AvaloniaResumeDecisionCoordinator(shellController);
        var shellPreferenceCommands = new ShellPreferenceCommands(
            shellPreferencesService,
            shellController,
            shortcutProfileService);
        var shortcutCommandExecutor = new ShortcutCommandExecutor(
            shellController,
            shellController,
            shellPreferencesService,
            shellPreferenceCommands,
            subtitleWorkflowController);

        return new AvaloniaShellDependencies(
            playbackHostRuntime,
            shellController,
            resumeDecisionCoordinator,
            shellPreferenceCommands,
            shellController,
            shellController,
            shellProjectionReader,
            shellPreferencesService,
            windowModeService,
            shellLibraryService,
            shortcutProfileService,
            shortcutCommandExecutor,
            resumePlaybackService,
            subtitleWorkflowController,
            credentialSetupService,
            filePickerService,
            new CompositeShellLifetime(
                shellController,
                shellProjectionReader as IDisposable,
                playbackHostRuntime,
                playbackBackendCoordinator,
                subtitleWorkflowController,
                shortcutProfileService as IDisposable,
                playbackBackend));
    }

    private static ShellPreferencesService CreateShellPreferencesService()
    {
        var shellPreferencesService = new ShellPreferencesService(new SettingsFacade());
        var settingsPath = Path.Combine(SecureSettingsStore.GetAppDataDirectory(), "player-settings.json");
        if (!File.Exists(settingsPath))
        {
            shellPreferencesService.ApplyLayoutChange(new ShellLayoutPreferencesChange(
                false,
                false,
                shellPreferencesService.Current.WindowMode));
            shellPreferencesService.ApplyShortcutProfileChange(new ShellShortcutProfileChange(CreateFirstRunShortcutProfile()));
        }

        return shellPreferencesService;
    }

    private static ShellShortcutProfile CreateFirstRunShortcutProfile()
    {
        var profile = ShellShortcutProfile.CreateDefault();
        profile.Bindings["seek_back_small"] = "Left";
        profile.Bindings["seek_forward_small"] = "Right";
        profile.Bindings["volume_up"] = "Up";
        profile.Bindings["volume_down"] = "Down";
        profile.Bindings["fullscreen"] = "F";
        profile.Bindings["exit_fullscreen"] = "Escape";
        profile.Bindings["mute"] = "M";
        profile.Bindings["subtitle_toggle"] = "S";
        return profile;
    }

    private sealed class CompositeShellLifetime : IDisposable
    {
        private readonly IDisposable? _shellController;
        private readonly IDisposable? _projectionReader;
        private readonly IDisposable? _playbackHostRuntime;
        private readonly IDisposable? _playbackBackendCoordinator;
        private readonly IDisposable? _subtitleWorkflowService;
        private readonly IDisposable? _shortcutProfileService;
        private readonly IAsyncDisposable? _playbackBackend;
        private bool _disposed;

        public CompositeShellLifetime(
            IDisposable? shellController,
            IDisposable? projectionReader,
            IDisposable? playbackHostRuntime,
            IDisposable? playbackBackendCoordinator,
            IDisposable? subtitleWorkflowService,
            IDisposable? shortcutProfileService,
            IAsyncDisposable? playbackBackend)
        {
            _shellController = shellController;
            _projectionReader = projectionReader;
            _playbackHostRuntime = playbackHostRuntime;
            _playbackBackendCoordinator = playbackBackendCoordinator;
            _subtitleWorkflowService = subtitleWorkflowService;
            _shortcutProfileService = shortcutProfileService;
            _playbackBackend = playbackBackend;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _projectionReader?.Dispose();
            _shellController?.Dispose();
            _playbackHostRuntime?.Dispose();
            _playbackBackendCoordinator?.Dispose();
            _subtitleWorkflowService?.Dispose();
            _shortcutProfileService?.Dispose();
            _playbackBackend?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
