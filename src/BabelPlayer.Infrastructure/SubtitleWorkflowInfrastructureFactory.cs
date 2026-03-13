using BabelPlayer.App;

namespace BabelPlayer.Infrastructure;

public sealed class SubtitleWorkflowInfrastructureFactory : ISubtitleWorkflowInfrastructureFactory
{
    private readonly IProviderCompositionFactory _providerCompositionFactory;
    private readonly IAiCredentialCoordinatorFactory _aiCredentialCoordinatorFactory;

    public SubtitleWorkflowInfrastructureFactory(
        IProviderCompositionFactory? providerCompositionFactory = null,
        IAiCredentialCoordinatorFactory? aiCredentialCoordinatorFactory = null)
    {
        _providerCompositionFactory = providerCompositionFactory ?? new ProviderCompositionFactory();
        _aiCredentialCoordinatorFactory = aiCredentialCoordinatorFactory ?? new AiCredentialCoordinatorFactory();
    }

    public SubtitleWorkflowInfrastructure Create(SubtitleWorkflowInfrastructureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var runtimeBootstrapService = new RuntimeBootstrapService(request.LogFactory);
        var providerComposition = _providerCompositionFactory.Create(
            request.CredentialFacade,
            request.EnvironmentVariableReader,
            request.LogFactory);
        var providerAvailabilityService = new ProviderAvailabilityService(providerComposition);
        var translationEngineFactory = new MtTranslationEngineFactory();

        return new SubtitleWorkflowInfrastructure(
            runtimeBootstrapService,
            providerAvailabilityService,
            new DefaultSubtitleSourceResolver(runtimeBootstrapService),
            new DefaultCaptionGenerator(providerComposition.Context, providerComposition.TranscriptionRegistry, request.LogFactory),
            new ProviderBackedSubtitleTranslator(
                providerComposition.Context,
                providerComposition.TranslationRegistry,
                translationEngineFactory,
                providerComposition.LocalRuntime,
                request.LogFactory),
            _aiCredentialCoordinatorFactory.Create(
                request.CredentialFacade,
                request.CredentialDialogService,
                request.EnvironmentVariableReader),
            new DefaultRuntimeProvisioner(
                runtimeBootstrapService,
                request.CredentialFacade,
                request.CredentialDialogService,
                request.FilePickerService,
                request.EnvironmentVariableReader));
    }
}
