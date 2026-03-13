using BabelPlayer.App;
using BabelPlayer.Core;
using BabelPlayer.Infrastructure;
using Whisper.net.Ggml;

namespace BabelPlayer.App.Tests;

internal static class TestWorkflowControllerFactory
{
    public static SubtitleWorkflowController Create(
        CredentialFacade? credentialFacade = null,
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
    {
        credentialFacade ??= new CredentialFacade();
        mediaSessionCoordinator ??= new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var environmentReader = environmentVariableReader ?? Environment.GetEnvironmentVariable;
        var transcriptionEngineFactory = new AsrTranscriptionEngineFactory();
        var translationEngineFactory = new MtTranslationEngineFactory();
        var providerCompositionFactory = new ProviderCompositionFactory(transcriptionEngineFactory, translationEngineFactory);
        var providerComposition = providerAvailabilityService is ProviderAvailabilityService concreteProviderAvailabilityService
            ? concreteProviderAvailabilityService.Composition
            : providerCompositionFactory.Create(credentialFacade, environmentReader);
        var availabilityService = providerAvailabilityService ?? new ProviderAvailabilityService(providerComposition);
        var workflowStateStore = new InMemorySubtitleWorkflowStateStore();
        var resolvedCaptionGenerator = captionGenerator ?? (transcribeVideoAsync is null
            ? new DefaultCaptionGenerator(providerComposition.Context, providerComposition.TranscriptionRegistry)
            : new DelegateCaptionGenerator(transcribeVideoAsync));

        var subtitleApplicationService = new SubtitleApplicationService(
            new DefaultSubtitleSourceResolver(runtimeBootstrapService ?? new RuntimeBootstrapService()),
            resolvedCaptionGenerator,
            subtitleTranslator ?? new ProviderBackedSubtitleTranslator(
                providerComposition.Context,
                providerComposition.TranslationRegistry,
                translationEngineFactory,
                providerComposition.LocalRuntime),
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
            mediaSessionCoordinator,
            workflowStateStore,
            availabilityService);

        return new SubtitleWorkflowController(
            subtitleApplicationService,
            new SubtitleWorkflowProjectionAdapter(workflowStateStore, mediaSessionCoordinator.Store),
            new SubtitlePresentationProjector());
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
                    LocalModelType = selection.LocalModelKey switch
                    {
                        "tiny" => Whisper.net.Ggml.GgmlType.Tiny,
                        "base" => Whisper.net.Ggml.GgmlType.Base,
                        "small" => Whisper.net.Ggml.GgmlType.Small,
                        "tiny-en" => Whisper.net.Ggml.GgmlType.TinyEn,
                        _ => null
                    },
                    CloudModel = selection.CloudModel
                },
                onFinal ?? (_ => { }),
                onProgress ?? (_ => { }),
                cancellationToken);
        }
    }
}
