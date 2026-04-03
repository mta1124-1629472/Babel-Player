using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Shared readiness gate for containerized providers.
/// A provider is only ready when the service is live and explicitly advertises
/// capability for that stage.
/// </summary>
public static class ContainerizedProviderReadiness
{
    private static readonly TimeSpan ExecutionProbeBudget = TimeSpan.FromSeconds(10);

    public static ProviderReadiness CheckTranscription(
        AppSettings settings,
        ContainerizedServiceProbe? probe = null,
        ApiKeyStore? keyStore = null) =>
        Check(settings, ContainerCapabilityStage.Transcription, probe);

    public static ProviderReadiness CheckTranslation(
        AppSettings settings,
        ContainerizedServiceProbe? probe = null,
        ApiKeyStore? keyStore = null) =>
        Check(settings, ContainerCapabilityStage.Translation, probe);

    public static ProviderReadiness CheckTts(
        AppSettings settings,
        ContainerizedServiceProbe? probe = null,
        ApiKeyStore? keyStore = null) =>
        Check(settings, ContainerCapabilityStage.Tts, probe);

    public static Task<ProviderReadiness> CheckTranscriptionForExecutionAsync(
        AppSettings settings,
        ContainerizedServiceProbe probe,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Transcription, probe, cancellationToken);

    public static Task<ProviderReadiness> CheckTranslationForExecutionAsync(
        AppSettings settings,
        ContainerizedServiceProbe probe,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Translation, probe, cancellationToken);

    public static Task<ProviderReadiness> CheckTtsForExecutionAsync(
        AppSettings settings,
        ContainerizedServiceProbe probe,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Tts, probe, cancellationToken);

    private static ProviderReadiness Check(
        AppSettings settings,
        ContainerCapabilityStage stage,
        ContainerizedServiceProbe? probe)
    {
        var serviceUrl = settings.EffectiveGpuServiceUrl;
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return new ProviderReadiness(false, "No GPU inference host URL configured.");

        var probeResult = probe?.GetCurrentOrStartBackgroundProbe(serviceUrl)
            ?? FromHealth(ContainerizedInferenceClient.CheckHealth(serviceUrl, timeoutSeconds: 2));

        return MapProbeResultToReadiness(settings, probeResult, stage);
    }

    private static async Task<ProviderReadiness> CheckForExecutionAsync(
        AppSettings settings,
        ContainerCapabilityStage stage,
        ContainerizedServiceProbe probe,
        CancellationToken cancellationToken)
    {
        var serviceUrl = settings.EffectiveGpuServiceUrl;
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return new ProviderReadiness(false, "No GPU inference host URL configured.");

        var probeResult = await probe.WaitForProbeAsync(
            serviceUrl,
            forceRefresh: true,
            waitTimeout: ExecutionProbeBudget,
            cancellationToken);

        return MapProbeResultToReadiness(settings, probeResult, stage);
    }

    internal static ProviderReadiness MapProbeResultToReadiness(
        AppSettings settings,
        ContainerizedProbeResult probeResult,
        ContainerCapabilityStage stage)
    {
        var hostLabel = GetHostLabel(settings);
        if (probeResult.State == ContainerizedProbeState.Checking)
        {
            return new ProviderReadiness(
                false,
                $"{hostLabel} is starting at {probeResult.ServiceUrl}...");
        }

        if (probeResult.State == ContainerizedProbeState.Unavailable)
        {
            var detail = string.IsNullOrWhiteSpace(probeResult.ErrorDetail)
                ? probeResult.ServiceUrl
                : $"{probeResult.ServiceUrl} ({probeResult.ErrorDetail})";
            return new ProviderReadiness(false, BuildUnreachableMessage(settings, probeResult.ServiceUrl, detail));
        }

        if (probeResult.Capabilities is null || !probeResult.Capabilities.IsReady(stage))
        {
            var detail = probeResult.Capabilities?.Detail(stage);
            var stageLabel = stage switch
            {
                ContainerCapabilityStage.Transcription => "transcription",
                ContainerCapabilityStage.Translation => "translation",
                _ => "TTS",
            };
            return new ProviderReadiness(
                false,
                BuildCapabilityNotReadyMessage(hostLabel, stageLabel, detail));
        }

        return ProviderReadiness.Ready;
    }

    private static ContainerizedProbeResult FromHealth(ContainerHealthStatus health) =>
        new(
            health.ServiceUrl,
            health.IsAvailable ? ContainerizedProbeState.Available : ContainerizedProbeState.Unavailable,
            DateTimeOffset.UtcNow,
            health.ErrorMessage,
            health.CudaAvailable,
            health.CudaVersion,
            health.Capabilities);

    private static string BuildUnreachableMessage(AppSettings settings, string serviceUrl, string detail)
    {
        var hostLabel = GetHostLabel(settings);
        if (settings.PreferredLocalGpuBackend == GpuHostBackend.ManagedVenv)
        {
            return $"Start your managed local GPU host at {serviceUrl}. Current probe: {detail}";
        }

        if (Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)))
        {
            return $"Start your local Docker GPU host at {serviceUrl}. Current probe: {detail}";
        }

        return $"Configured {hostLabel} is not reachable: {detail}";
    }

    private static string GetHostLabel(AppSettings settings) =>
        settings.PreferredLocalGpuBackend == GpuHostBackend.ManagedVenv
            ? "Managed local GPU host"
            : "Docker GPU host";

    private static string BuildCapabilityNotReadyMessage(string hostLabel, string stageLabel, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return $"{hostLabel} is live but {stageLabel} capability is not ready.";

        if (detail.Contains("warming", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("probe", StringComparison.OrdinalIgnoreCase))
        {
            return $"{hostLabel} is live but {stageLabel} capability is still warming (missing {stageLabel} capability): {detail}";
        }

        return $"{hostLabel} is live but missing {stageLabel} capability: {detail}";
    }
}
