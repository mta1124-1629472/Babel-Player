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
        PlaylistController playlistController,
        PlaybackSessionController playbackSessionController,
        CredentialFacade credentialFacade,
        Func<IDisposable> suppressDialogPresentation);
}

public sealed record ShellDependencies
{
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
    public ShellDependencies Create(
        MainWindow ownerWindow,
        Grid rootGrid,
        PlaylistController playlistController,
        PlaybackSessionController playbackSessionController,
        CredentialFacade credentialFacade,
        Func<IDisposable> suppressDialogPresentation)
    {
        var filePickerService = new WinUIFilePickerService(ownerWindow);
        var windowModeService = new WinUIWindowModeService(ownerWindow);
        windowModeService.SetWindowIcon(Path.Combine(AppContext.BaseDirectory, "BabelPlayer.ico"));

        var credentialDialogService = new WinUICredentialDialogService(rootGrid, suppressDialogPresentation);
        var runtimeBootstrapService = new RuntimeBootstrapService();
        var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var providerComposition = ProviderAvailabilityCompositionFactory.Create(credentialFacade, Environment.GetEnvironmentVariable);
        var providerAvailabilityService = new ProviderAvailabilityService(providerComposition);
        var workflowStateStore = new InMemorySubtitleWorkflowStateStore();
        var subtitleApplicationService = new SubtitleApplicationService(
            new DefaultSubtitleSourceResolver(),
            new DefaultCaptionGenerator(providerComposition.Context, providerComposition.TranscriptionRegistry),
            new ProviderBackedSubtitleTranslator(providerComposition.Context, providerComposition.TranslationRegistry),
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
                Environment.GetEnvironmentVariable),
            credentialFacade,
            mediaSessionCoordinator,
            workflowStateStore,
            providerAvailabilityService);
        var subtitleWorkflowController = new SubtitleWorkflowController(
            subtitleApplicationService,
            new SubtitleWorkflowProjectionAdapter(workflowStateStore, mediaSessionCoordinator.Store),
            new SubtitlePresentationProjector());
        var playbackBackend = new MpvPlaybackBackend();
        var playbackBackendCoordinator = new PlaybackBackendCoordinator(playbackBackend, mediaSessionCoordinator);
        var videoPresenter = new MpvVideoPresenter();
        var subtitlePresenter = new DetachedWindowSubtitlePresenter(ownerWindow);
        var shellProjectionService = new ShellProjectionService(mediaSessionCoordinator.Store);
        var libraryBrowserService = new LibraryBrowserService();
        var resumePlaybackService = new ResumePlaybackService();
        var shellController = new ShellController(
            playlistController,
            playbackSessionController,
            playbackBackend,
            subtitleWorkflowController,
            libraryBrowserService,
            resumePlaybackService);

        var stageCoordinator = new StageCoordinator(
            rootGrid,
            windowModeService,
            videoPresenter,
            subtitlePresenter,
            () => new FullscreenOverlayWindow(WindowNative.GetWindowHandle(ownerWindow)));

        return new ShellDependencies
        {
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
