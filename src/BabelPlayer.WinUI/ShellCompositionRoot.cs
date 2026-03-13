using BabelPlayer.App;
using BabelPlayer.Core;
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
    public required IBabelLogFactory LogFactory { get; init; }
    public required IAppDiagnosticsContext DiagnosticsContext { get; init; }
    public required IFilePickerService FilePickerService { get; init; }
    public required WinUIWindowModeService WindowModeService { get; init; }
    public required WinUICredentialDialogService CredentialDialogService { get; init; }
    public required IRuntimeBootstrapService RuntimeBootstrapService { get; init; }
    public required MediaSessionCoordinator MediaSessionCoordinator { get; init; }
    public required ISubtitleWorkflowShellService SubtitleWorkflowService { get; init; }
    public required IPlaybackBackend PlaybackBackend { get; init; }
    public required PlaybackBackendCoordinator PlaybackBackendCoordinator { get; init; }
    public required IVideoPresenter VideoPresenter { get; init; }
    public required ISubtitlePresenter SubtitlePresenter { get; init; }
    public required IShellPreferencesService ShellPreferencesService { get; init; }
    public required IShellPreferenceCommands ShellPreferenceCommands { get; init; }
    public required IShellLibraryService ShellLibraryService { get; init; }
    public required ShellProjectionService ShellProjectionService { get; init; }
    public required IQueueProjectionReader QueueProjectionReader { get; init; }
    public required IQueueCommands QueueCommands { get; init; }
    public required IShellPlaybackCommands ShellPlaybackCommands { get; init; }
    public required ICredentialSetupService CredentialSetupService { get; init; }
    public required IShortcutProfileService ShortcutProfileService { get; init; }
    public required IShortcutCommandExecutor ShortcutCommandExecutor { get; init; }
    public required IDisposable ShellControllerLifetime { get; init; }
    public required StageCoordinator StageCoordinator { get; init; }
}

public sealed class ShellCompositionRoot : IShellCompositionRoot
{
    private readonly IBabelLogFactory _logFactory;
    private readonly IAppDiagnosticsContext _diagnosticsContext;

    public ShellCompositionRoot(IBabelLogFactory? logFactory = null, IAppDiagnosticsContext? diagnosticsContext = null)
    {
        _logFactory = logFactory ?? NullBabelLogFactory.Instance;
        _diagnosticsContext = diagnosticsContext ?? new AppDiagnosticsContext();
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
        var runtimeBootstrapService = new RuntimeBootstrapService(_logFactory);
        var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var providerComposition = ProviderAvailabilityCompositionFactory.Create(credentialFacade, Environment.GetEnvironmentVariable, _logFactory);
        var providerAvailabilityService = new ProviderAvailabilityService(providerComposition);
        var workflowStateStore = new InMemorySubtitleWorkflowStateStore();
        var aiCredentialCoordinator = new DefaultAiCredentialCoordinator(
            credentialFacade,
            credentialDialogService,
            Environment.GetEnvironmentVariable,
            MtService.ValidateApiKeyAsync,
            MtService.ValidateTranslationProviderAsync);
        var runtimeProvisioner = new DefaultRuntimeProvisioner(
            runtimeBootstrapService,
            credentialFacade,
            credentialDialogService,
            filePickerService,
            Environment.GetEnvironmentVariable,
            _logFactory);
        var subtitleApplicationService = new SubtitleApplicationService(
            new DefaultSubtitleSourceResolver(_logFactory),
            new DefaultCaptionGenerator(providerComposition.Context, providerComposition.TranscriptionRegistry, _logFactory),
            new ProviderBackedSubtitleTranslator(providerComposition.Context, providerComposition.TranslationRegistry, _logFactory),
            aiCredentialCoordinator,
            runtimeProvisioner,
            credentialFacade,
            mediaSessionCoordinator,
            workflowStateStore,
            providerAvailabilityService,
            _logFactory);
        var subtitleWorkflowController = new SubtitleWorkflowController(
            subtitleApplicationService,
            new SubtitleWorkflowProjectionAdapter(workflowStateStore, mediaSessionCoordinator.Store),
            new SubtitlePresentationProjector());
        ISubtitleWorkflowShellService subtitleWorkflowService = subtitleWorkflowController;
        var playbackBackend = new MpvPlaybackBackend(_logFactory);
        var playbackBackendCoordinator = new PlaybackBackendCoordinator(playbackBackend, mediaSessionCoordinator);
        var videoPresenter = new MpvVideoPresenter(_logFactory);
        var subtitlePresenter = new DetachedWindowSubtitlePresenter(ownerWindow);
        var shellLibraryService = new ShellLibraryService(new LibraryBrowserService(), shellPreferencesService);
        var shellProjectionService = new ShellProjectionService(mediaSessionCoordinator.Store);
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
            shellPreferencesService,
            _logFactory);
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
            LogFactory = _logFactory,
            DiagnosticsContext = _diagnosticsContext,
            FilePickerService = filePickerService,
            WindowModeService = windowModeService,
            CredentialDialogService = credentialDialogService,
            RuntimeBootstrapService = runtimeBootstrapService,
            MediaSessionCoordinator = mediaSessionCoordinator,
            SubtitleWorkflowService = subtitleWorkflowService,
            PlaybackBackend = playbackBackend,
            PlaybackBackendCoordinator = playbackBackendCoordinator,
            VideoPresenter = videoPresenter,
            SubtitlePresenter = subtitlePresenter,
            ShellPreferencesService = shellPreferencesService,
            ShellPreferenceCommands = shellPreferenceCommands,
            ShellLibraryService = shellLibraryService,
            ShellProjectionService = shellProjectionService,
            QueueProjectionReader = shellController,
            QueueCommands = shellController,
            ShellPlaybackCommands = shellController,
            CredentialSetupService = credentialSetupService,
            ShortcutProfileService = shortcutProfileService,
            ShortcutCommandExecutor = shortcutCommandExecutor,
            ShellControllerLifetime = shellController,
            StageCoordinator = stageCoordinator
        };
    }
}
