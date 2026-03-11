using BabelPlayer.Core;

namespace BabelPlayer.App;

internal sealed class GoogleTranslationProviderAdapter : ITranslationProvider
{
    public TranslationProvider Provider => TranslationProvider.Google;

    public bool IsConfigured(ProviderAvailabilityContext context)
        => ProviderAvailabilityUtilities.TryGetGoogleOptions(context) is not null;

    public Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        return ProviderAvailabilityUtilities.TranslateWithMtServiceAsync(
            request.Texts,
            ProviderAvailabilityUtilities.TryGetGoogleOptions(context),
            null,
            cancellationToken);
    }
}
