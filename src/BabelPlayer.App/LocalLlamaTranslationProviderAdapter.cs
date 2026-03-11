using BabelPlayer.Core;

namespace BabelPlayer.App;

internal sealed class LocalLlamaTranslationProviderAdapter : ITranslationProvider
{
    public LocalLlamaTranslationProviderAdapter(TranslationProvider provider)
    {
        Provider = provider;
    }

    public TranslationProvider Provider { get; }

    public bool IsConfigured(ProviderAvailabilityContext context)
        => ProviderAvailabilityUtilities.ResolveLlamaCppServerPath(context) is not null;

    public Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        var model = Provider switch
        {
            TranslationProvider.LocalHyMt15_1_8B => OfflineTranslationModel.HyMt15_1_8B,
            TranslationProvider.LocalHyMt15_7B => OfflineTranslationModel.HyMt15_7B,
            _ => OfflineTranslationModel.None
        };

        return ProviderAvailabilityUtilities.TranslateWithMtServiceAsync(
            request.Texts,
            null,
            new LocalTranslationOptions(model, ProviderAvailabilityUtilities.ResolveLlamaCppServerPath(context)),
            cancellationToken);
    }
}
