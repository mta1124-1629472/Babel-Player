using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Shared readiness gate for containerized providers.
/// A provider is only ready when the service is live and explicitly advertises
/// capability for that stage.
/// </summary>
public static class ContainerizedProviderReadiness
{
    public static ProviderReadiness CheckTranscription(AppSettings settings, ApiKeyStore? keyStore = null) =>
        Check(settings, ContainerCapabilityStage.Transcription);

    public static ProviderReadiness CheckTranslation(AppSettings settings, ApiKeyStore? keyStore = null) =>
        Check(settings, ContainerCapabilityStage.Translation);

    public static ProviderReadiness CheckTts(AppSettings settings, ApiKeyStore? keyStore = null) =>
        Check(settings, ContainerCapabilityStage.Tts);

    private static ProviderReadiness Check(
        AppSettings settings,
        ContainerCapabilityStage stage)
    {
        var serviceUrl = settings.EffectiveContainerizedServiceUrl;
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return new ProviderReadiness(false, "No containerized service URL configured.");

        var health = ContainerizedInferenceClient.CheckHealth(serviceUrl, timeoutSeconds: 2);
        if (!health.IsAvailable)
        {
            var detail = string.IsNullOrWhiteSpace(health.ErrorMessage)
                ? health.ServiceUrl
                : $"{health.ServiceUrl} ({health.ErrorMessage})";
            return new ProviderReadiness(
                false,
                $"Containerized inference service is unavailable: {detail}");
        }

        if (health.Capabilities is null || !health.Capabilities.IsReady(stage))
        {
            var detail = health.Capabilities?.Detail(stage);
            var stageLabel = stage switch
            {
                ContainerCapabilityStage.Transcription => "transcription",
                ContainerCapabilityStage.Translation => "translation",
                _ => "TTS",
            };
            return new ProviderReadiness(
                false,
                string.IsNullOrWhiteSpace(detail)
                    ? $"Containerized inference service is live but not ready for {stageLabel}."
                    : $"Containerized inference service is not ready for {stageLabel}: {detail}");
        }

        return ProviderReadiness.Ready;
    }
}
