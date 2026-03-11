namespace BabelPlayer.App;

internal sealed class LlamaCppRuntimeAdapter : ILocalModelRuntime
{
    public string RuntimeId => "llama.cpp";

    public string? ResolveExecutablePath(ProviderAvailabilityContext context)
        => ProviderAvailabilityUtilities.ResolveLlamaCppServerPath(context);
}
