using BabelPlayer.App;
using BabelPlayer.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;
using WinRT.Interop;

namespace BabelPlayer.WinUI;

public interface IShellCompositionRoot
{
    ShellDependencies Create(
        MainWindow ownerWindow,
        Grid rootGrid,
        PlaybackQueueController playbackQueueController,
        Func<IDisposable> suppressDialogPresentation);
}

public sealed record ShellDependencies
{
    public required IAppLogFactory LogFactory { get; init; }
    public required IAppDiagnosticsState DiagnosticsContext { get; init; }
    public required IFilePickerService FilePickerService { get; init; }
    public required WinUIWindowModeService WindowModeService { get; init; }
    public required ICredentialDialogService CredentialDialogService { get; init; }
    public required ISubtitleWorkflowShellService SubtitleWorkflowService { get; init; }
    public required IPlaybackHostRuntime PlaybackHostRuntime { get; init; }
    public required IVideoPresenter VideoPresenter { get; init; }
    public required ISubtitlePresenter SubtitlePresenter { get; init; }
    public required IShellPreferencesService ShellPreferencesService { get; init; }
    public required IShellPreferenceCommands ShellPreferenceCommands { get; init; }
    public required IShellLibraryService ShellLibraryService { get; init; }
    public required IShellProjectionReader ShellProjectionReader { get; init; }
    public required IQueueProjectionReader QueueProjectionReader { get; init; }
    public required IQueueCommands QueueCommands { get; init; }
    public required IShellPlaybackCommands ShellPlaybackCommands { get; init; }
    public required ICredentialSetupService CredentialSetupService { get; init; }
    public required IShortcutProfileService ShortcutProfileService { get; init; }
    public required IShortcutCommandExecutor ShortcutCommandExecutor { get; init; }
    public required IDisposable ShellLifetime { get; init; }
    public required StageCoordinator StageCoordinator { get; init; }
}

public sealed class ShellCompositionRoot : IShellCompositionRoot
{
    private readonly IAppTelemetryBootstrap _telemetry;

    public ShellCompositionRoot(IAppTelemetryBootstrap? telemetry = null)
    {
        _telemetry = telemetry ?? new AppTelemetryBootstrap();
    }

    public ShellDependencies Create(
        MainWindow ownerWindow,
        Grid rootGrid,
        PlaybackQueueController playbackQueueController,
        Func<IDisposable> suppressDialogPresentation)
    {
        var credentialFacade = new CredentialFacade();
        var filePickerService = new WinUIFilePickerService(ownerWindow);
        var windowModeService = new WinUIWindowModeService(ownerWindow);
        windowModeService.SetWindowIcon(Path.Combine(AppContext.BaseDirectory, "BabelPlayer.ico"));
        var shellPreferencesService = new ShellPreferencesService(new SettingsFacade());
        var shortcutProfileService = new ShortcutProfileService(shellPreferencesService);
        var credentialDialogService = new WinUICredentialDialogService(rootGrid, shortcutProfileService, suppressDialogPresentation);
        var runtimeBootstrapService = new RuntimeBootstrapService();
        var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var transcriptionEngineFactory = new AsrTranscriptionEngineFactory();
        var translationEngineFactory = new MtTranslationEngineFactory();
        var providerCompositionFactory = new ProviderCompositionFactory(transcriptionEngineFactory, translationEngineFactory);
        var providerComposition = providerCompositionFactory.Create(credentialFacade, Environment.GetEnvironmentVariable);
        var providerAvailabilityService = new ProviderAvailabilityService(providerComposition);
        var workflowStateStore = new InMemorySubtitleWorkflowStateStore();
        var aiCredentialCoordinator = new AiCredentialCoordinatorFactory().Create(
            credentialFacade,
            credentialDialogService,
            Environment.GetEnvironmentVariable);
        var runtimeProvisioner = new DefaultRuntimeProvisioner(
            runtimeBootstrapService,
            credentialFacade,
            credentialDialogService,
            filePickerService,
            Environment.GetEnvironmentVariable);
        var subtitleApplicationService = new SubtitleApplicationService(
            new DefaultSubtitleSourceResolver(runtimeBootstrapService),
            new DefaultCaptionGenerator(providerComposition.Context, providerComposition.TranscriptionRegistry),
            new ProviderBackedSubtitleTranslator(
                providerComposition.Context,
                providerComposition.TranslationRegistry,
                translationEngineFactory,
                providerComposition.LocalRuntime),
            aiCredentialCoordinator,
            runtimeProvisioner,
            credentialFacade,
            mediaSessionCoordinator,
            workflowStateStore,
            providerAvailabilityService);
        var subtitleWorkflowController = new SubtitleWorkflowController(
            subtitleApplicationService,
            new SubtitleWorkflowProjectionAdapter(workflowStateStore, mediaSessionCoordinator.Store),
            new SubtitlePresentationProjector());
        ISubtitleWorkflowShellService subtitleWorkflowService = subtitleWorkflowController;
        var playbackBackend = new MpvPlaybackBackend(runtimeBootstrapService);
        var playbackBackendCoordinator = new PlaybackBackendCoordinator(playbackBackend, mediaSessionCoordinator);
        var playbackHostRuntime = new PlaybackHostRuntimeAdapter(playbackBackend);
        var videoPresenter = new MpvVideoPresenter(_telemetry.LogFactory);
        var subtitlePresenter = new DetachedWindowSubtitlePresenter(ownerWindow);
        var shellLibraryService = new ShellLibraryService(new LibraryBrowserService(), shellPreferencesService);
        IShellProjectionReader shellProjectionReader = new ShellProjectionService(mediaSessionCoordinator.Store);
        var resumePlaybackService = new ResumePlaybackService();
        var credentialSetupService = new CredentialSetupService(
            credentialFacade,
            providerAvailabilityService,
            aiCredentialCoordinator,
            runtimeProvisioner,
            Environment.GetEnvironmentVariable);
        var shellController = new ShellController(
            playbackQueueController,
            playbackBackend,
            subtitleWorkflowController,
            new LibraryBrowserService(),
            resumePlaybackService,
            shellPreferencesService);
        var shellPreferenceCommands = new ShellPreferenceCommands(
            shellPreferencesService,
            shellController,
            shortcutProfileService);
        var shortcutCommandExecutor = new ShortcutCommandExecutor(
            shellController,
            shellController,
            shellPreferencesService,
            shellPreferenceCommands,
            subtitleWorkflowService);

        var stageCoordinator = new StageCoordinator(
            rootGrid,
            windowModeService,
            videoPresenter,
            subtitlePresenter,
            () => new FullscreenOverlayWindow(WindowNative.GetWindowHandle(ownerWindow)));

        return new ShellDependencies
        {
            LogFactory = _telemetry.LogFactory,
            DiagnosticsContext = _telemetry.DiagnosticsState,
            FilePickerService = filePickerService,
            WindowModeService = windowModeService,
            CredentialDialogService = credentialDialogService,
            SubtitleWorkflowService = subtitleWorkflowService,
            PlaybackHostRuntime = playbackHostRuntime,
            VideoPresenter = videoPresenter,
            SubtitlePresenter = subtitlePresenter,
            ShellPreferencesService = shellPreferencesService,
            ShellPreferenceCommands = shellPreferenceCommands,
            ShellLibraryService = shellLibraryService,
            ShellProjectionReader = shellProjectionReader,
            QueueProjectionReader = shellController,
            QueueCommands = shellController,
            ShellPlaybackCommands = shellController,
            CredentialSetupService = credentialSetupService,
            ShortcutProfileService = shortcutProfileService,
            ShortcutCommandExecutor = shortcutCommandExecutor,
            ShellLifetime = new CompositeShellLifetime(
                shellController,
                shellProjectionReader as IDisposable,
                playbackHostRuntime,
                playbackBackendCoordinator,
                playbackBackend,
                subtitleWorkflowService),
            StageCoordinator = stageCoordinator
        };
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
