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
        CredentialFacade credentialFacade,
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
    public required SubtitleWorkflowController SubtitleWorkflowController { get; init; }
    public required IPlaybackBackend PlaybackBackend { get; init; }
    public required PlaybackBackendCoordinator PlaybackBackendCoordinator { get; init; }
    public required IVideoPresenter VideoPresenter { get; init; }
    public required ISubtitlePresenter SubtitlePresenter { get; init; }
    public required ShellProjectionService ShellProjectionService { get; init; }
    public required ShellController ShellController { get; init; }
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
        CredentialFacade credentialFacade,
        Func<IDisposable> suppressDialogPresentation)
    {
        var filePickerService = new WinUIFilePickerService(ownerWindow);
        var windowModeService = new WinUIWindowModeService(ownerWindow);
        windowModeService.SetWindowIcon(Path.Combine(AppContext.BaseDirectory, "BabelPlayer.ico"));

        var credentialDialogService = new WinUICredentialDialogService(rootGrid, suppressDialogPresentation);
        var runtimeBootstrapService = new RuntimeBootstrapService(_logFactory);
        var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var providerComposition = ProviderAvailabilityCompositionFactory.Create(credentialFacade, Environment.GetEnvironmentVariable, _logFactory);
        var providerAvailabilityService = new ProviderAvailabilityService(providerComposition);
        var workflowStateStore = new InMemorySubtitleWorkflowStateStore();
        var subtitleApplicationService = new SubtitleApplicationService(
            new DefaultSubtitleSourceResolver(_logFactory),
            new DefaultCaptionGenerator(providerComposition.Context, providerComposition.TranscriptionRegistry, _logFactory),
            new ProviderBackedSubtitleTranslator(providerComposition.Context, providerComposition.TranslationRegistry, _logFactory),
            new DefaultAiCredentialCoordinator(
                credentialFacade,
                credentialDialogService,
                Environment.GetEnvironmentVariable,
                MtService.ValidateApiKeyAsync,
                MtService.ValidateTranslationProviderAsync),
            new DefaultRuntimeProvisioner(
                runtimeBootstrapService,
                credentialFacade,
                credentialDialogService,
                filePickerService,
                Environment.GetEnvironmentVariable,
                _logFactory),
            credentialFacade,
            mediaSessionCoordinator,
            workflowStateStore,
            providerAvailabilityService,
            _logFactory);
        var subtitleWorkflowController = new SubtitleWorkflowController(
            subtitleApplicationService,
            new SubtitleWorkflowProjectionAdapter(workflowStateStore, mediaSessionCoordinator.Store),
            new SubtitlePresentationProjector());
        var playbackBackend = new MpvPlaybackBackend(_logFactory);
        var playbackBackendCoordinator = new PlaybackBackendCoordinator(playbackBackend, mediaSessionCoordinator);
        var videoPresenter = new MpvVideoPresenter();
        var subtitlePresenter = new DetachedWindowSubtitlePresenter(ownerWindow);
        var shellProjectionService = new ShellProjectionService(mediaSessionCoordinator.Store);
        var libraryBrowserService = new LibraryBrowserService();
        var resumePlaybackService = new ResumePlaybackService();
        var shellController = new ShellController(
            playbackQueueController,
            playbackBackend,
            subtitleWorkflowController,
            libraryBrowserService,
            resumePlaybackService,
            _logFactory);

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
            SubtitleWorkflowController = subtitleWorkflowController,
            PlaybackBackend = playbackBackend,
            PlaybackBackendCoordinator = playbackBackendCoordinator,
            VideoPresenter = videoPresenter,
            SubtitlePresenter = subtitlePresenter,
            ShellProjectionService = shellProjectionService,
            ShellController = shellController,
            StageCoordinator = stageCoordinator
        };
    }
}
