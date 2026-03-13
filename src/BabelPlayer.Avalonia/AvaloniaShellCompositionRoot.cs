using Avalonia.Controls;
using BabelPlayer.App;
using BabelPlayer.Infrastructure;
using BabelPlayer.Playback.Mpv;

namespace BabelPlayer.Avalonia;

public sealed record AvaloniaShellDependencies(
    IPlaybackHostRuntime PlaybackHostRuntime,
    IShellPreferencesService ShellPreferencesService,
    ISubtitleWorkflowShellService SubtitleWorkflowService,
    IShellProjectionReader ShellProjectionReader,
    IQueueCommands QueueCommands,
    IShellPlaybackCommands ShellPlaybackCommands,
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
    private readonly ISubtitleWorkflowInfrastructureFactory _subtitleWorkflowInfrastructureFactory;

    public AvaloniaShellCompositionRoot(ISubtitleWorkflowInfrastructureFactory? subtitleWorkflowInfrastructureFactory = null)
    {
        _subtitleWorkflowInfrastructureFactory = subtitleWorkflowInfrastructureFactory ?? new SubtitleWorkflowInfrastructureFactory();
    }

    public AvaloniaShellDependencies Create(Window ownerWindow)
    {
        var credentialFacade = new CredentialFacade();
        var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var workflowStateStore = new InMemorySubtitleWorkflowStateStore();
        var filePickerService = new AvaloniaFilePickerService(ownerWindow);
        var credentialDialogService = new AvaloniaCredentialDialogService(ownerWindow);
        var shellPreferencesService = new ShellPreferencesService(new SettingsFacade());
        var subtitleInfrastructure = _subtitleWorkflowInfrastructureFactory.Create(new SubtitleWorkflowInfrastructureRequest(
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
        var shellController = new ShellController(
            playbackQueueController,
            playbackBackend,
            subtitleWorkflowController,
            new LibraryBrowserService(),
            new ResumePlaybackService(),
            shellPreferencesService);

        return new AvaloniaShellDependencies(
            playbackHostRuntime,
            shellPreferencesService,
            subtitleWorkflowController,
            shellProjectionReader,
            shellController,
            shellController,
            filePickerService,
            new CompositeShellLifetime(
                shellController,
                shellProjectionReader as IDisposable,
                playbackHostRuntime,
                playbackBackendCoordinator,
                playbackBackend,
                subtitleWorkflowController));
    }

    private sealed class CompositeShellLifetime : IDisposable
    {
        private readonly IDisposable? _shellController;
        private readonly IDisposable? _projectionReader;
        private readonly IDisposable? _playbackHostRuntime;
        private readonly IDisposable? _playbackBackendCoordinator;
        private readonly IAsyncDisposable? _playbackBackend;
        private readonly IDisposable? _subtitleWorkflowService;
        private bool _disposed;

        public CompositeShellLifetime(
            IDisposable? shellController,
            IDisposable? projectionReader,
            IDisposable? playbackHostRuntime,
            IDisposable? playbackBackendCoordinator,
            IAsyncDisposable? playbackBackend,
            IDisposable? subtitleWorkflowService)
        {
            _shellController = shellController;
            _projectionReader = projectionReader;
            _playbackHostRuntime = playbackHostRuntime;
            _playbackBackendCoordinator = playbackBackendCoordinator;
            _playbackBackend = playbackBackend;
            _subtitleWorkflowService = subtitleWorkflowService;
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
