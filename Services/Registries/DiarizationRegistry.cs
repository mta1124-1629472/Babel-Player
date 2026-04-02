using System;
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

    public DiarizationRegistry(AppLog log)
    {
        _log = log;
    }

    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders() =>
    [
        new ProviderDescriptor(
            ProviderNames.PyannoteLocal,
            "Pyannote (Local)",
            false,
            null,
            ["pyannote/speaker-diarization-3.1"],
            IsImplemented: false,
            Notes: "Requires pyannote.audio Python package and HuggingFace model download."),
    ];

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

    public IDiarizationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null)
    {
        // PLACEHOLDER — no diarization providers are implemented in this build.
        throw new PipelineProviderException(
            $"Diarization provider '{providerId}' is not implemented yet. " +
            "No diarization providers are available in this build.");
    }
}
