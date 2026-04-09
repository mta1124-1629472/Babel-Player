using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services.Registries;

public interface IDiarizationProvider
{
    ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore);
    Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default);
    Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, CancellationToken ct = default);
}

public interface IDiarizationRegistry
{
    IReadOnlyList<ProviderDescriptor> GetAvailableProviders();
    IDiarizationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null);
    ProviderReadiness CheckReadiness(string providerId, AppSettings settings, ApiKeyStore? keyStore);
}

public sealed class DiarizationRegistry : IDiarizationRegistry
{
    private readonly AppLog _log;
    private readonly ContainerizedServiceProbe? _containerizedProbe;
    private readonly ConcurrentDictionary<string, ContainerizedInferenceClient> _clientCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of <see cref="DiarizationRegistry"/> with the given logging facility and an optional probe for containerized services.
    /// </summary>
    /// <param name="log">Application logger used by the registry and the providers it creates.</param>
    /// <param name="containerizedProbe">Optional probe to check containerized inference service health and availability; may be <c>null</c>.</param>
    public DiarizationRegistry(AppLog log, ContainerizedServiceProbe? containerizedProbe = null)
    {
        _log = log;
        _containerizedProbe = containerizedProbe;
    }

    /// <summary>
    /// Lists the diarization providers available in this registry.
    /// </summary>
    /// <returns>A read-only list of <see cref="ProviderDescriptor"/> objects describing each available diarization provider.</returns>
    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders() =>
    [
        new ProviderDescriptor(
            ProviderNames.NemoLocal,
            "NeMo",
            false,
            null,
            [ProviderNames.NemoDiarizationAlias],
            SupportedRuntimes: [InferenceRuntime.Containerized],
            DefaultRuntime: InferenceRuntime.Containerized,
            IsImplemented: true,
            Notes: "Uses the containerized NeMo ClusteringDiarizer endpoint."),
        new ProviderDescriptor(
            ProviderNames.WeSpeakerLocal,
            "WeSpeaker",
            false,
            null,
            [ProviderNames.WeSpeakerDiarizationAlias],
            SupportedRuntimes: [InferenceRuntime.Containerized],
            DefaultRuntime: InferenceRuntime.Containerized,
            IsImplemented: true,
            Notes: "Uses the containerized WeSpeaker CPU fallback endpoint."),
    ];

    /// <summary>
    /// Determines whether the diarization provider identified by <paramref name="providerId"/> is available, implemented, and ready to use.
    /// </summary>
    /// <param name="providerId">The identifier of the diarization provider to check.</param>
    /// <param name="settings">Application settings that influence provider configuration.</param>
    /// <param name="keyStore">Optional API key store used by some providers; may be null.</param>
    /// <returns>
    /// A <see cref="ProviderReadiness"/> describing readiness. The result will indicate failure with a diagnostic message if the provider is unknown or not implemented; otherwise it reflects the provider's own readiness status.
    /// </returns>
    public ProviderReadiness CheckReadiness(string providerId, AppSettings settings, ApiKeyStore? keyStore)
    {
        var desc = GetAvailableProviders().FirstOrDefault(p => p.Id == providerId);
        if (desc == null)
            return new ProviderReadiness(false, $"Unknown diarization provider '{providerId}'.");
        if (!desc.IsImplemented)
            return new ProviderReadiness(false, $"Provider '{desc.DisplayName}' is not implemented yet.");

        var provider = CreateProvider(providerId, settings, keyStore);
        return provider.CheckReadiness(settings, keyStore);
    }

    /// <summary>
    /// Create an IDiarizationProvider instance for the specified provider identifier.
    /// </summary>
    /// <param name="providerId">The provider identifier to instantiate (e.g., ProviderNames.NemoLocal or ProviderNames.WeSpeakerLocal).</param>
    /// <param name="settings">Application settings used to configure the provider (its EffectiveContainerizedServiceUrl is used to construct the containerized client).</param>
    /// <param name="keyStore">API key store (accepted but not used by the current provider implementations).</param>
    /// <returns>The instantiated diarization provider configured according to the provided settings.</returns>
    /// <exception cref="PipelineProviderException">Thrown when the specified providerId is not implemented.</exception>
    public IDiarizationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null)
    {
        var serviceUrl = ContainerizedInferenceClient.NormalizeBaseUrl(settings.EffectiveContainerizedServiceUrl);
        var client = _clientCache.GetOrAdd(serviceUrl, normalizedServiceUrl => new ContainerizedInferenceClient(normalizedServiceUrl, _log));

        return providerId switch
        {
            ProviderNames.NemoLocal => new NemoContainerizedDiarizationProvider(
                client,
                _log,
                _containerizedProbe),
            ProviderNames.WeSpeakerLocal => new WeSpeakerContainerizedDiarizationProvider(
                client,
                _log,
                _containerizedProbe),
            _ => throw new PipelineProviderException(
                $"Diarization provider '{providerId}' is not implemented. " +
                "Select an implemented provider in Settings.")
        };
    }

}
