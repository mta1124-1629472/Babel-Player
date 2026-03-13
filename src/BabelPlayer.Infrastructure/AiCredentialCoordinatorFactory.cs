using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.Infrastructure;

public sealed class AiCredentialCoordinatorFactory : IAiCredentialCoordinatorFactory
{
    public IAiCredentialCoordinator Create(
        CredentialFacade credentialFacade,
        ICredentialDialogService? credentialDialogService,
        Func<string, string?>? environmentVariableReader = null)
    {
        return new DefaultAiCredentialCoordinator(
            credentialFacade,
            credentialDialogService,
            environmentVariableReader ?? Environment.GetEnvironmentVariable,
            MtService.ValidateApiKeyAsync,
            MtService.ValidateTranslationProviderAsync);
    }
}
