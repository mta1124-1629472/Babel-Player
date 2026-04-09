using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

public sealed class FakeDiarizationProvider : IDiarizationProvider
{
    private readonly Func<DiarizationRequest, DiarizationResult> _resultFactory;

    /// <summary>
    /// Initializes a test fake diarization provider that produces configurable diarization results and exposes mutable readiness.
    /// </summary>
    /// <param name="resultFactory">Factory used to create a <see cref="DiarizationResult"/> for each request; if null, a factory that returns a successful empty result is used.</param>
    /// <param name="readiness">Initial provider readiness; if null, defaults to ready with no message.</param>
    public FakeDiarizationProvider(
        Func<DiarizationRequest, DiarizationResult>? resultFactory = null,
        ProviderReadiness? readiness = null)
    {
        _resultFactory = resultFactory ?? (_ => new DiarizationResult(true, [], 0, null));
        Readiness = readiness ?? new ProviderReadiness(true, null);
    }

    public ProviderReadiness Readiness { get; set; }

    public DiarizationRequest? LastRequest { get; private set; }

    /// <summary>
/// Gets the current readiness state of the provider.
/// </summary>
/// <returns>The provider's current <see cref="ProviderReadiness"/>.</returns>
public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore) => Readiness;

    /// <summary>
    /// Ensures the provider is ready and reports completion progress when provided.
    /// </summary>
    /// <param name="settings">Application settings context used for readiness checks.</param>
    /// <param name="progress">If non-null, reports 1.0 to indicate readiness completion.</param>
    /// <param name="ct">Cancellation token observed before the readiness result is returned.</param>
    /// <returns>`true` if the provider is ready, `false` otherwise.</returns>
    public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<bool>(ct);

        progress?.Report(1.0);
        return Task.FromResult(Readiness.IsReady);
    }

    /// <summary>
    /// Generates a diarization result for the specified request and records that request as the most recent one.
    /// </summary>
    /// <param name="request">The diarization request to process.</param>
    /// <param name="ct">Cancellation token observed before the result is produced.</param>
    /// <returns>The diarization result produced for the given request.</returns>
    public Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<DiarizationResult>(ct);

        LastRequest = request;
        return Task.FromResult(_resultFactory(request));
    }
}

public sealed class FakeDiarizationRegistry(
    params (string ProviderId, string DisplayName, IDiarizationProvider Provider)[] providers)
    : IDiarizationRegistry
{
    private readonly IReadOnlyDictionary<string, (ProviderDescriptor Descriptor, IDiarizationProvider Provider)> _providers =
        providers.ToDictionary(
            entry => entry.ProviderId,
            entry => (
                new ProviderDescriptor(
                    entry.ProviderId,
                    entry.DisplayName,
                    RequiresApiKey: false,
                    CredentialKey: null,
                    SupportedModels: [],
                    SupportedRuntimes: [InferenceRuntime.Containerized],
                    DefaultRuntime: InferenceRuntime.Containerized,
                    IsImplemented: true,
                    Notes: null),
                entry.Provider),
            StringComparer.Ordinal);

    /// <summary>
        /// Enumerates provider descriptors available in the registry.
        /// </summary>
        /// <returns>A read-only list of provider descriptors registered with the registry.</returns>
        public IReadOnlyList<ProviderDescriptor> GetAvailableProviders() =>
        [.. _providers.Values.Select(entry => entry.Descriptor)];

    /// <summary>
    /// Gets the registered diarization provider for the specified provider identifier.
    /// </summary>
    /// <param name="providerId">The identifier of the diarization provider to retrieve.</param>
    /// <param name="settings">Application settings used when creating or selecting the provider.</param>
    /// <param name="keyStore">Optional API key store available to the provider.</param>
    /// <returns>The <see cref="IDiarizationProvider"/> registered for <paramref name="providerId"/>.</returns>
    /// <exception cref="PipelineProviderException">Thrown when no provider is registered for <paramref name="providerId"/>.</exception>
    public IDiarizationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (_providers.TryGetValue(providerId, out var entry))
            return entry.Provider;

        throw new PipelineProviderException($"Unknown diarization provider '{providerId}'.");
    }

    /// <summary>
    /// Checks the readiness of the diarization provider with the given identifier.
    /// </summary>
    /// <param name="providerId">The identifier of the provider to check.</param>
    /// <returns>A <see cref="ProviderReadiness"/> describing whether the provider is ready; if the providerId is unknown, returns a not-ready readiness with an explanatory message.</returns>
    public ProviderReadiness CheckReadiness(string providerId, AppSettings settings, ApiKeyStore? keyStore)
    {
        if (_providers.TryGetValue(providerId, out var entry))
            return entry.Provider.CheckReadiness(settings, keyStore);

        return new ProviderReadiness(false, $"Unknown diarization provider '{providerId}'.");
    }
}

public static class FakeDiarizationFactory
{
    public static IDiarizationRegistry CreateDefaultRegistry() =>
        new FakeDiarizationRegistry(
            (ProviderNames.NemoLocal, "NeMo", new FakeDiarizationProvider()),
            (ProviderNames.WeSpeakerLocal, "WeSpeaker", new FakeDiarizationProvider()));
}
