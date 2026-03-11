using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class SubtitleWorkflowController
{
    private readonly SubtitlePresentationProjector _subtitlePresentationProjector = new();
    private readonly SubtitleApplicationService _subtitleApplicationService;
    private readonly SubtitleWorkflowProjectionAdapter _projectionAdapter;
    private readonly IMediaSessionStore _mediaSessionStore;

    public SubtitleWorkflowController()
        : this(
            new CredentialFacade(),
            null,
            null,
            new RuntimeBootstrapService(),
            null,
            Environment.GetEnvironmentVariable,
            MtService.ValidateApiKeyAsync,
            MtService.ValidateTranslationProviderAsync)
    {
    }

    public SubtitleWorkflowController(
        CredentialFacade credentialFacade,
        ICredentialDialogService? credentialDialogService,
        IFilePickerService? filePickerService,
        IRuntimeBootstrapService? runtimeBootstrapService,
        Func<string, string?>? environmentVariableReader,
        Func<string, CancellationToken, Task>? validateOpenAiApiKeyAsync,
        Func<CloudTranslationOptions, CancellationToken, Task>? validateTranslationProviderAsync,
        Func<string, CaptionGenerationOptions, Action<TranscriptChunk>, Action<ModelTransferProgress>, CancellationToken, Task<IReadOnlyList<SubtitleCue>>>? transcribeVideoAsync = null,
        IProviderAvailabilityService? providerAvailabilityService = null,
        ICaptionGenerator? captionGenerator = null,
        ISubtitleTranslator? subtitleTranslator = null,
        IAiCredentialCoordinator? aiCredentialCoordinator = null,
        IRuntimeProvisioner? runtimeProvisioner = null)
        : this(
            credentialFacade,
            credentialDialogService,
            filePickerService,
            runtimeBootstrapService,
            null,
            environmentVariableReader,
            validateOpenAiApiKeyAsync,
            validateTranslationProviderAsync,
            transcribeVideoAsync,
            providerAvailabilityService,
            captionGenerator,
            subtitleTranslator,
            aiCredentialCoordinator,
            runtimeProvisioner)
    {
    }

    public SubtitleWorkflowController(
        CredentialFacade credentialFacade,
        ICredentialDialogService? credentialDialogService = null,
        IFilePickerService? filePickerService = null,
        IRuntimeBootstrapService? runtimeBootstrapService = null,
        MediaSessionCoordinator? mediaSessionCoordinator = null,
        Func<string, string?>? environmentVariableReader = null,
        Func<string, CancellationToken, Task>? validateOpenAiApiKeyAsync = null,
        Func<CloudTranslationOptions, CancellationToken, Task>? validateTranslationProviderAsync = null,
        Func<string, CaptionGenerationOptions, Action<TranscriptChunk>, Action<ModelTransferProgress>, CancellationToken, Task<IReadOnlyList<SubtitleCue>>>? transcribeVideoAsync = null,
        IProviderAvailabilityService? providerAvailabilityService = null,
        ICaptionGenerator? captionGenerator = null,
        ISubtitleTranslator? subtitleTranslator = null,
        IAiCredentialCoordinator? aiCredentialCoordinator = null,
        IRuntimeProvisioner? runtimeProvisioner = null)
        : this(ComposeCompatibilityDependencies(
            credentialFacade,
            credentialDialogService,
            filePickerService,
            runtimeBootstrapService,
            mediaSessionCoordinator,
            environmentVariableReader,
            validateOpenAiApiKeyAsync,
            validateTranslationProviderAsync,
            transcribeVideoAsync,
            providerAvailabilityService,
            captionGenerator,
            subtitleTranslator,
            aiCredentialCoordinator,
            runtimeProvisioner))
    {
    }

    private SubtitleWorkflowController(CompatibilityDependencies dependencies)
        : this(
            dependencies.SubtitleApplicationService,
            new SubtitleWorkflowProjectionAdapter(
                dependencies.WorkflowStateStore,
                dependencies.SubtitleApplicationService.MediaSessionStore))
    {
    }

    public SubtitleWorkflowController(
        SubtitleApplicationService subtitleApplicationService,
        SubtitleWorkflowProjectionAdapter projectionAdapter,
        SubtitlePresentationProjector? subtitlePresentationProjector = null)
    {
        _subtitleApplicationService = subtitleApplicationService;
        _projectionAdapter = projectionAdapter;
        if (subtitlePresentationProjector is not null)
        {
            _subtitlePresentationProjector = subtitlePresentationProjector;
        }

        _mediaSessionStore = _subtitleApplicationService.MediaSessionStore;
        _projectionAdapter.SnapshotChanged += HandleSnapshotChanged;
        _subtitleApplicationService.StatusChanged += HandleStatusChanged;
        _subtitleApplicationService.RuntimeInstallProgressChanged += HandleRuntimeInstallProgressChanged;
    }

    public event Action<SubtitleWorkflowSnapshot>? SnapshotChanged;
    public event Action<string>? StatusChanged;
    public event Action<RuntimeInstallProgress>? RuntimeInstallProgressChanged;

    public IMediaSessionStore MediaSessionStore => _mediaSessionStore;

    public SubtitleWorkflowSnapshot Snapshot => _projectionAdapter.Current;

    public IReadOnlyList<SubtitleCue> CurrentCues => _subtitleApplicationService.CurrentCues;

    public bool HasCurrentCues => _subtitleApplicationService.HasCurrentCues;

    public SubtitleOverlayPresentation GetOverlayPresentation(
        SubtitleRenderMode renderMode,
        bool subtitlesVisible = true,
        bool sourceOnlyOverrideForCurrentVideo = false)
    {
        var presentation = _subtitlePresentationProjector.Build(
            _mediaSessionStore.Snapshot,
            renderMode,
            subtitlesVisible,
            sourceOnlyOverrideForCurrentVideo);
        return new SubtitleOverlayPresentation
        {
            IsVisible = presentation.IsVisible,
            PrimaryText = presentation.PrimaryText,
            SecondaryText = presentation.SecondaryText
        };
    }

    public SubtitleRenderMode GetEffectiveRenderMode(
        SubtitleRenderMode requestedMode,
        bool sourceOnlyOverrideForCurrentVideo = false)
    {
        return _subtitlePresentationProjector.GetEffectiveRenderMode(
            _mediaSessionStore.Snapshot,
            requestedMode,
            sourceOnlyOverrideForCurrentVideo);
    }

    public void ExportCurrentSubtitles(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SubtitleFileService.ExportSrt(path, CurrentCues);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _subtitleApplicationService.InitializeAsync(cancellationToken);

    public SubtitleRenderMode ToggleSource(SubtitleRenderMode current)
    {
        return current switch
        {
            SubtitleRenderMode.Off => SubtitleRenderMode.SourceOnly,
            SubtitleRenderMode.SourceOnly => SubtitleRenderMode.Off,
            SubtitleRenderMode.TranslationOnly => SubtitleRenderMode.Dual,
            SubtitleRenderMode.Dual => SubtitleRenderMode.TranslationOnly,
            _ => SubtitleRenderMode.TranslationOnly
        };
    }

    public SubtitleRenderMode ToggleTranslation(SubtitleRenderMode current)
    {
        return current switch
        {
            SubtitleRenderMode.Off => SubtitleRenderMode.TranslationOnly,
            SubtitleRenderMode.SourceOnly => SubtitleRenderMode.Dual,
            SubtitleRenderMode.TranslationOnly => SubtitleRenderMode.Off,
            SubtitleRenderMode.Dual => SubtitleRenderMode.SourceOnly,
            _ => SubtitleRenderMode.TranslationOnly
        };
    }

    public SubtitleStyleSettings UpdateStyle(
        SubtitleStyleSettings current,
        double? sourceFontSize = null,
        double? translationFontSize = null,
        double? backgroundOpacity = null,
        double? bottomMargin = null,
        double? dualSpacing = null,
        string? sourceForegroundHex = null,
        string? translationForegroundHex = null)
    {
        return current with
        {
            SourceFontSize = sourceFontSize ?? current.SourceFontSize,
            TranslationFontSize = translationFontSize ?? current.TranslationFontSize,
            BackgroundOpacity = backgroundOpacity ?? current.BackgroundOpacity,
            BottomMargin = bottomMargin ?? current.BottomMargin,
            DualSpacing = dualSpacing ?? current.DualSpacing,
            SourceForegroundHex = sourceForegroundHex ?? current.SourceForegroundHex,
            TranslationForegroundHex = translationForegroundHex ?? current.TranslationForegroundHex
        };
    }

    public Task<bool> SelectTranscriptionModelAsync(string modelKey, CancellationToken cancellationToken = default, bool suppressStatus = false)
        => _subtitleApplicationService.SelectTranscriptionModelAsync(modelKey, cancellationToken, suppressStatus);

    public Task<bool> SelectTranslationModelAsync(string modelKey, CancellationToken cancellationToken = default)
        => _subtitleApplicationService.SelectTranslationModelAsync(modelKey, cancellationToken);

    public Task SetTranslationEnabledAsync(bool enabled, bool lockPreference = true, CancellationToken cancellationToken = default)
        => _subtitleApplicationService.SetTranslationEnabledAsync(enabled, lockPreference, cancellationToken);

    public Task SetAutoTranslateEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        => _subtitleApplicationService.SetAutoTranslateEnabledAsync(enabled, cancellationToken);

    public Task<SubtitleLoadResult> LoadMediaSubtitlesAsync(string videoPath, CancellationToken cancellationToken = default)
        => _subtitleApplicationService.LoadMediaSubtitlesAsync(videoPath, cancellationToken);

    public Task<SubtitleLoadResult> ImportExternalSubtitlesAsync(string path, bool autoLoaded = false, CancellationToken cancellationToken = default)
        => _subtitleApplicationService.ImportExternalSubtitlesAsync(path, autoLoaded, cancellationToken);

    public Task<SubtitleLoadResult> ImportEmbeddedSubtitleTrackAsync(string videoPath, MediaTrackInfo track, CancellationToken cancellationToken = default)
        => _subtitleApplicationService.ImportEmbeddedSubtitleTrackAsync(videoPath, track, cancellationToken);

    public void UpdatePlaybackPosition(TimeSpan position)
        => _subtitleApplicationService.UpdatePlaybackPosition(position);

    private void HandleSnapshotChanged(SubtitleWorkflowSnapshot snapshot)
    {
        SnapshotChanged?.Invoke(snapshot);
    }

    private void HandleStatusChanged(string message)
    {
        StatusChanged?.Invoke(message);
    }

    private void HandleRuntimeInstallProgressChanged(RuntimeInstallProgress progress)
    {
        RuntimeInstallProgressChanged?.Invoke(progress);
    }

    private static CompatibilityDependencies ComposeCompatibilityDependencies(
        CredentialFacade credentialFacade,
        ICredentialDialogService? credentialDialogService,
        IFilePickerService? filePickerService,
        IRuntimeBootstrapService? runtimeBootstrapService,
        MediaSessionCoordinator? mediaSessionCoordinator,
        Func<string, string?>? environmentVariableReader,
        Func<string, CancellationToken, Task>? validateOpenAiApiKeyAsync,
        Func<CloudTranslationOptions, CancellationToken, Task>? validateTranslationProviderAsync,
        Func<string, CaptionGenerationOptions, Action<TranscriptChunk>, Action<ModelTransferProgress>, CancellationToken, Task<IReadOnlyList<SubtitleCue>>>? transcribeVideoAsync,
        IProviderAvailabilityService? providerAvailabilityService,
        ICaptionGenerator? captionGenerator,
        ISubtitleTranslator? subtitleTranslator,
        IAiCredentialCoordinator? aiCredentialCoordinator,
        IRuntimeProvisioner? runtimeProvisioner)
    {
        var coordinator = mediaSessionCoordinator ?? new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var environmentReader = environmentVariableReader ?? Environment.GetEnvironmentVariable;
        var providerComposition = providerAvailabilityService is ProviderAvailabilityService concreteProviderAvailabilityService
            ? concreteProviderAvailabilityService.Composition
            : ProviderAvailabilityCompositionFactory.Create(credentialFacade, environmentReader);
        var availabilityService = providerAvailabilityService ?? new ProviderAvailabilityService(providerComposition);
        var workflowStateStore = new InMemorySubtitleWorkflowStateStore();
        var resolvedCaptionGenerator = captionGenerator ?? (transcribeVideoAsync is null
            ? new DefaultCaptionGenerator(providerComposition.Context, providerComposition.TranscriptionRegistry)
            : new DelegateCaptionGenerator(transcribeVideoAsync));

        return new CompatibilityDependencies(
            new SubtitleApplicationService(
                new DefaultSubtitleSourceResolver(),
                resolvedCaptionGenerator,
                subtitleTranslator ?? new ProviderBackedSubtitleTranslator(providerComposition.Context, providerComposition.TranslationRegistry),
                aiCredentialCoordinator ?? new DefaultAiCredentialCoordinator(
                    credentialFacade,
                    credentialDialogService,
                    environmentReader,
                    validateOpenAiApiKeyAsync ?? MtService.ValidateApiKeyAsync,
                    validateTranslationProviderAsync ?? MtService.ValidateTranslationProviderAsync),
                runtimeProvisioner ?? new DefaultRuntimeProvisioner(
                    runtimeBootstrapService ?? new RuntimeBootstrapService(),
                    credentialFacade,
                    credentialDialogService,
                    filePickerService,
                    environmentReader),
                credentialFacade,
                coordinator,
                workflowStateStore,
                availabilityService),
            workflowStateStore);
    }

    private sealed class DelegateCaptionGenerator : ICaptionGenerator
    {
        private readonly Func<string, CaptionGenerationOptions, Action<TranscriptChunk>, Action<ModelTransferProgress>, CancellationToken, Task<IReadOnlyList<SubtitleCue>>> _transcribeVideoAsync;

        public DelegateCaptionGenerator(
            Func<string, CaptionGenerationOptions, Action<TranscriptChunk>, Action<ModelTransferProgress>, CancellationToken, Task<IReadOnlyList<SubtitleCue>>> transcribeVideoAsync)
        {
            _transcribeVideoAsync = transcribeVideoAsync;
        }

        public Task<IReadOnlyList<SubtitleCue>> GenerateCaptionsAsync(
            string videoPath,
            TranscriptionModelSelection selection,
            string? languageHint,
            Action<TranscriptChunk>? onFinal,
            Action<ModelTransferProgress>? onProgress,
            CancellationToken cancellationToken)
        {
            return _transcribeVideoAsync(
                videoPath,
                new CaptionGenerationOptions
                {
                    Mode = selection.Provider == TranscriptionProvider.Cloud ? CaptionTranscriptionMode.Cloud : CaptionTranscriptionMode.Local,
                    LanguageHint = languageHint,
                    LocalModelType = selection.LocalModelType,
                    CloudModel = selection.CloudModel
                },
                onFinal ?? (_ => { }),
                onProgress ?? (_ => { }),
                cancellationToken);
        }
    }

    private sealed record CompatibilityDependencies(
        SubtitleApplicationService SubtitleApplicationService,
        InMemorySubtitleWorkflowStateStore WorkflowStateStore);
}
