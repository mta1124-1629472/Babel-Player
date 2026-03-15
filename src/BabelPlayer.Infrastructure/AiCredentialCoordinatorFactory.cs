using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.Infrastructure;

public sealed class AiCredentialCoordinatorFactory : IAiCredentialCoordinatorFactory
{
    public IAiCredentialCoordinator Create(
        ICredentialStore credentialStore,
        ICredentialDialogService? credentialDialogService,
        Func<string, string?>? environmentVariableReader = null)
    {
        return new DefaultAiCredentialCoordinator(
            credentialStore,
            credentialDialogService,
            environmentVariableReader ?? Environment.GetEnvironmentVariable,
            MtService.ValidateApiKeyAsync,
            MtService.ValidateTranslationProviderAsync);
    }
}
