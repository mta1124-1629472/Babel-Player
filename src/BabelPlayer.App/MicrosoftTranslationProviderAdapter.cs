using BabelPlayer.Core;

namespace BabelPlayer.App;

internal sealed class MicrosoftTranslationProviderAdapter : ITranslationProvider
{
    public TranslationProvider Provider => TranslationProvider.MicrosoftTranslator;

    public bool IsConfigured(ProviderAvailabilityContext context)
        => ProviderAvailabilityUtilities.TryGetMicrosoftOptions(context) is not null;

    public Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        return ProviderAvailabilityUtilities.TranslateWithMtServiceAsync(
            request.Texts,
            ProviderAvailabilityUtilities.TryGetMicrosoftOptions(context),
            null,
            cancellationToken,
            context.LogFactory);
    }
}
