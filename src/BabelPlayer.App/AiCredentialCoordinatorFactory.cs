using BabelPlayer.Core;

namespace BabelPlayer.App;

public static class AiCredentialCoordinatorFactory
{
    public static IAiCredentialCoordinator CreateDefault(
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
