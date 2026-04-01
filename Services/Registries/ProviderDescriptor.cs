using System.Collections.Generic;

namespace Babel.Player.Services.Registries;

/// <summary>
/// Describes a provider available for a pipeline stage.
/// <paramref name="IsImplemented"/> must be false for providers whose
/// <c>CreateProvider</c> path still throws <see cref="System.NotImplementedException"/>.
/// The UI should disable or hide unimplemented providers.
/// </summary>
public sealed record ProviderDescriptor(
    string Id,
    string DisplayName,
    bool RequiresApiKey,
    string? CredentialKey,
    IReadOnlyList<string> SupportedModels,
    bool IsImplemented = true,
    string? Notes = null);

/// <summary>
/// Result of a provider readiness check. A ready provider has
/// <see cref="IsReady"/> = true and all other fields null/false.
/// When not ready, <see cref="BlockingReason"/> explains why to the user.
/// </summary>
public sealed record ProviderReadiness(
    bool IsReady,
    string? BlockingReason = null,
    bool RequiresModelDownload = false,
    string? ModelDownloadDescription = null)
{
    public static readonly ProviderReadiness Ready = new(true);
}

