using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Shared readiness gate for the containerized inference providers.
/// Validates both configuration and live service health so the UI does not
/// advertise the provider as usable when the service is down.
/// </summary>
public static class ContainerizedProviderReadiness
{
    public static ProviderReadiness Check(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (string.IsNullOrWhiteSpace(settings.ContainerizedServiceUrl))
            return new ProviderReadiness(false, "No containerized service URL configured in Settings.");

        var health = ContainerizedInferenceClient.CheckHealth(
            settings.ContainerizedServiceUrl,
            timeoutSeconds: 2);

        if (health.IsAvailable)
            return ProviderReadiness.Ready;

        var detail = string.IsNullOrWhiteSpace(health.ErrorMessage)
            ? health.ServiceUrl
            : $"{health.ServiceUrl} ({health.ErrorMessage})";

        return new ProviderReadiness(
            false,
            $"Containerized inference service is unavailable: {detail}");
    }
}
