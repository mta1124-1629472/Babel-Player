using BabelPlayer.Core;

namespace BabelPlayer.App;

internal sealed class OpenAiTranslationProviderAdapter : ITranslationProvider
{
    public TranslationProvider Provider => TranslationProvider.OpenAi;

    public bool IsConfigured(ProviderAvailabilityContext context)
        => ProviderAvailabilityUtilities.HasOpenAiApiKey(context);

    public Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        var apiKey = context.EnvironmentVariableReader("OPENAI_API_KEY") ?? context.CredentialFacade.GetOpenAiApiKey();
        return ProviderAvailabilityUtilities.TranslateWithMtServiceAsync(
            request.Texts,
            new CloudTranslationOptions(CloudTranslationProvider.OpenAi, apiKey!.Trim(), request.Selection.CloudModel),
            null,
            cancellationToken);
    }
}
