using System;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Separates an input track into vocals and instrumental stems.
/// </summary>
public interface IVocalSeparationProvider
{
    Task<VocalSeparationResult> SeparateVocalsAsync(
        VocalSeparationRequest request,
        CancellationToken cancellationToken = default);

    ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
        => ProviderReadiness.Ready;

    Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default)
        => Task.FromResult(true);
}
