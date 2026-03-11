using BabelPlayer.Core;

namespace BabelPlayer.App;

internal sealed class DeepLTranslationProviderAdapter : ITranslationProvider
{
    public TranslationProvider Provider => TranslationProvider.DeepL;

    public bool IsConfigured(ProviderAvailabilityContext context)
        => ProviderAvailabilityUtilities.TryGetDeepLOptions(context) is not null;

    public Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        return ProviderAvailabilityUtilities.TranslateWithMtServiceAsync(
            request.Texts,
            ProviderAvailabilityUtilities.TryGetDeepLOptions(context),
            null,
            cancellationToken);
    }
}
