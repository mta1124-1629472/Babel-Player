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
            IsImplemented: true,
            Notes: "Requires pyannote.audio Python package and HuggingFace model acceptance."),
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
        return providerId switch
        {
            ProviderNames.PyannoteLocal => new PyannoteDiarizationProvider(
                _log,
                ResolveHuggingFaceToken(keyStore, settings)),
            _ => throw new PipelineProviderException(
                $"Diarization provider '{providerId}' is not implemented. " +
                "Select an implemented provider in Settings.")
        };
    }

    /// <summary>
    /// Resolves the HuggingFace token: keyStore value takes precedence over the settings
    /// fallback. Whitespace-only values are treated as absent.
    /// </summary>
    private static string? ResolveHuggingFaceToken(ApiKeyStore? keyStore, AppSettings settings)
    {
        var storeToken = keyStore?.GetKey(CredentialKeys.HuggingFace)?.Trim();
        if (!string.IsNullOrWhiteSpace(storeToken))
            return storeToken;

        var settingsToken = settings.DiarizationHuggingFaceToken?.Trim();
        return string.IsNullOrWhiteSpace(settingsToken) ? null : settingsToken;
    }
}
