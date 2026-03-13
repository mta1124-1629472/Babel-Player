using Avalonia.Controls;
using BabelPlayer.App;
using BabelPlayer.Infrastructure;
using BabelPlayer.Playback.Mpv;

namespace BabelPlayer.Avalonia;

public sealed record AvaloniaShellDependencies(
    IPlaybackHostRuntime PlaybackHostRuntime,
    IShellPlaybackCommands ShellPlaybackCommands,
    IQueueCommands QueueCommands,
    IQueueProjectionReader QueueProjectionReader,
    IShellProjectionReader ShellProjectionReader,
    IShellPreferencesService ShellPreferencesService,
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
        var shellPreferencesService = new ShellPreferencesService(new SettingsFacade());
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
            new ResumePlaybackService(),
            shellPreferencesService);

        return new AvaloniaShellDependencies(
            playbackHostRuntime,
            shellController,
            shellController,
            shellController,
            shellProjectionReader,
            shellPreferencesService,
            subtitleWorkflowController,
            credentialSetupService,
            filePickerService,
            new CompositeShellLifetime(
                shellController,
                shellProjectionReader as IDisposable,
                playbackHostRuntime,
                playbackBackendCoordinator,
                subtitleWorkflowController,
                playbackBackend));
    }

    private sealed class CompositeShellLifetime : IDisposable
    {
        private readonly IDisposable? _shellController;
        private readonly IDisposable? _projectionReader;
        private readonly IDisposable? _playbackHostRuntime;
        private readonly IDisposable? _playbackBackendCoordinator;
        private readonly IDisposable? _subtitleWorkflowService;
        private readonly IAsyncDisposable? _playbackBackend;
        private bool _disposed;

        public CompositeShellLifetime(
            IDisposable? shellController,
            IDisposable? projectionReader,
            IDisposable? playbackHostRuntime,
            IDisposable? playbackBackendCoordinator,
            IDisposable? subtitleWorkflowService,
            IAsyncDisposable? playbackBackend)
        {
            _shellController = shellController;
            _projectionReader = projectionReader;
            _playbackHostRuntime = playbackHostRuntime;
            _playbackBackendCoordinator = playbackBackendCoordinator;
            _subtitleWorkflowService = subtitleWorkflowService;
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
            _playbackBackend?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
