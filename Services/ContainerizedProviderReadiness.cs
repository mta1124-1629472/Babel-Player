using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private static readonly TimeSpan CapabilityWarmupBudget = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CapabilityWarmupRetryDelay = TimeSpan.FromSeconds(2);

    internal readonly record struct ExecutionWaitOptions(
        TimeSpan ExecutionProbeBudget,
        TimeSpan CapabilityWarmupBudget,
        TimeSpan CapabilityWarmupRetryDelay)
    {
        public static ExecutionWaitOptions Default { get; } =
            new(
                ContainerizedProviderReadiness.ExecutionProbeBudget,
                ContainerizedProviderReadiness.CapabilityWarmupBudget,
                ContainerizedProviderReadiness.CapabilityWarmupRetryDelay);
    }

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

    /// <summary>
        /// Determines whether the configured containerized GPU host is ready to perform TTS (text-to-speech) operations.
        /// </summary>
        /// <returns>
        /// A <see cref="ProviderReadiness"/> describing whether the TTS capability is available and, when not ready, a human-readable detail explaining the state.
        /// </returns>
        public static ProviderReadiness CheckTts(
        AppSettings settings,
        ContainerizedServiceProbe? probe = null,
        ApiKeyStore? keyStore = null) =>
        Check(settings, ContainerCapabilityStage.Tts, probe);

    /// <summary>
        /// Determines whether the configured containerized GPU host exposes a ready diarization capability.
        /// </summary>
        /// <param name="providerId">Optional diarization provider identifier to check; if empty, the value from <c>settings.DiarizationProvider</c> is used.</param>
        /// <returns>A <see cref="ProviderReadiness"/> indicating whether diarization (or the specified diarization provider) is ready; when not ready the result contains a user-facing detail message.</returns>
        public static ProviderReadiness CheckDiarization(
        AppSettings settings,
        string providerId,
        ContainerizedServiceProbe? probe = null,
        ApiKeyStore? keyStore = null) =>
        Check(settings, ContainerCapabilityStage.Diarization, probe, providerId);

    /// <summary>
        /// Waits for the containerized transcription capability to become ready for execution and reports its readiness.
        /// </summary>
        /// <param name="settings">Application settings used to locate and label the GPU inference host.</param>
        /// <param name="probe">Probe used to query and wait for the host's health and capability state.</param>
        /// <param name="cancellationToken">Token to cancel the wait operation.</param>
        /// <returns>A <see cref="ProviderReadiness"/> describing whether the transcription capability is available for execution; when not ready, includes a message explaining why.</returns>
    public static Task<ProviderReadiness> CheckTranscriptionForExecutionAsync(
        AppSettings settings,
        ContainerizedServiceProbe probe,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Transcription, probe, cancellationToken, waitOptions: ExecutionWaitOptions.Default);

    public static Task<ProviderReadiness> CheckTranslationForExecutionAsync(
        AppSettings settings,
        ContainerizedServiceProbe probe,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Translation, probe, cancellationToken, waitOptions: ExecutionWaitOptions.Default);

    /// <summary>
        /// Checks whether the containerized TTS capability is ready for execution by waiting for the provided probe to report readiness.
        /// </summary>
        /// <param name="probe">Probe used to query and wait for the containerized service's status.</param>
        /// <param name="cancellationToken">Token that can be used to cancel the wait for probe readiness.</param>
        /// <returns>A <see cref="ProviderReadiness"/> describing whether the TTS capability is ready and providing a human-readable status detail.</returns>
    public static Task<ProviderReadiness> CheckTtsForExecutionAsync(
        AppSettings settings,
        ContainerizedServiceProbe probe,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Tts, probe, cancellationToken, waitOptions: ExecutionWaitOptions.Default);

    /// <summary>
        /// Checks whether the diarization capability is ready for execution on the configured containerized GPU host, optionally targeting a specific provider ID; this may wait briefly while the host reports its readiness state.
        /// </summary>
        /// <param name="providerId">Optional provider identifier to check readiness for a specific diarization provider; if empty, the configured default provider is used.</param>
        /// <returns>A <see cref="ProviderReadiness"/> describing whether diarization is ready and including a human-readable detail message.</returns>
    public static Task<ProviderReadiness> CheckDiarizationForExecutionAsync(
        AppSettings settings,
        string providerId,
        ContainerizedServiceProbe probe,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Diarization, probe, cancellationToken, providerId, ExecutionWaitOptions.Default);

    internal static Task<ProviderReadiness> CheckDiarizationForExecutionAsync(
        AppSettings settings,
        string providerId,
        ContainerizedServiceProbe probe,
        ExecutionWaitOptions waitOptions,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Diarization, probe, cancellationToken, providerId, waitOptions);

    internal static Task<ProviderReadiness> CheckTtsForExecutionAsync(
        AppSettings settings,
        ContainerizedServiceProbe probe,
        ExecutionWaitOptions waitOptions,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Tts, probe, cancellationToken, waitOptions: waitOptions);

    /// <summary>
    /// Determines readiness of a containerized GPU provider for a specific capability stage.
    /// </summary>
    /// <param name="settings">Application settings used to locate the GPU service host.</param>
    /// <param name="stage">The capability stage to check (e.g., Transcription, Translation, Tts, Diarization).</param>
    /// <param name="probe">Optional service probe to consult; if null, a short health check is performed.</param>
    /// <param name="providerId">Optional provider identifier used for provider-specific readiness checks (applies to TTS and Diarization).</param>
    /// <returns>A <see cref="ProviderReadiness"/> that is ready when the requested stage (and provider when specified) is available; otherwise contains a failure state and human-readable detail.</returns>
    private static ProviderReadiness Check(
        AppSettings settings,
        ContainerCapabilityStage stage,
        ContainerizedServiceProbe? probe,
        string? providerId = null)
    {
        var serviceUrl = settings.EffectiveGpuServiceUrl;
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return new ProviderReadiness(false, "No GPU inference host URL configured.");

        var probeResult = probe?.GetCurrentOrStartBackgroundProbe(serviceUrl)
            ?? FromHealth(ContainerizedInferenceClient.CheckHealth(serviceUrl, timeoutSeconds: 2));

        return MapProbeResultToReadiness(settings, probeResult, stage, providerId);
    }

    /// <summary>
    /// Waits for the containerized GPU host to report readiness for the specified capability and maps the probe result to a ProviderReadiness suitable for execution-time checks.
    /// </summary>
    /// <param name="settings">Application settings used to determine the GPU service URL and host labeling.</param>
    /// <param name="stage">The capability stage to check (e.g., Transcription, Translation, Tts, Diarization).</param>
    /// <param name="probe">Probe instance used to query and wait for the host's probe state.</param>
    /// <param name="cancellationToken">Token to cancel waiting and warmup retries.</param>
    /// <param name="providerId">Optional provider identifier for provider-specific readiness selection (used for TTS and Diarization); when null or empty, the configured default provider from settings is used.</param>
    /// <returns>A ProviderReadiness that indicates whether the requested capability is ready for execution and contains a human-readable detail message when not ready.</returns>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is canceled while waiting for the probe or during warmup retries.</exception>
    private static async Task<ProviderReadiness> CheckForExecutionAsync(
        AppSettings settings,
        ContainerCapabilityStage stage,
        ContainerizedServiceProbe probe,
        CancellationToken cancellationToken,
        string? providerId = null,
        ExecutionWaitOptions waitOptions = default)
    {
        waitOptions = waitOptions == default ? ExecutionWaitOptions.Default : waitOptions;

        var serviceUrl = settings.EffectiveGpuServiceUrl;
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return new ProviderReadiness(false, "No GPU inference host URL configured.");

        var probeResult = await probe.WaitForProbeAsync(
            serviceUrl,
            forceRefresh: true,
            waitTimeout: waitOptions.ExecutionProbeBudget,
            cancellationToken).ConfigureAwait(false);

        if (IsCapabilityActivelyWarming(probeResult, stage, settings, providerId))
        {
            var warmupSw = Stopwatch.StartNew();
            while (warmupSw.Elapsed < waitOptions.CapabilityWarmupBudget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(waitOptions.CapabilityWarmupRetryDelay, cancellationToken).ConfigureAwait(false);
                probeResult = await probe.WaitForProbeAsync(
                    serviceUrl,
                    forceRefresh: true,
                    waitTimeout: waitOptions.ExecutionProbeBudget,
                    cancellationToken).ConfigureAwait(false);
                if (!IsCapabilityActivelyWarming(probeResult, stage, settings, providerId))
                    break;
            }
        }

        return MapProbeResultToReadiness(settings, probeResult, stage, providerId);
    }

    /// <summary>
    /// Determines whether the specified capability on the containerized host is in an active warmup state that warrants retrying probe checks.
    /// </summary>
    /// <param name="probeResult">The latest probe result for the containerized host.</param>
    /// <param name="stage">The capability stage to evaluate (e.g., Transcription, Tts, Diarization).</param>
    /// <param name="settings">Application settings used to resolve provider selection when applicable.</param>
    /// <param name="providerId">Optional provider identifier to evaluate provider-specific readiness for stages that support it; may be null.</param>
    /// <returns>`true` if the capability is available at the host level but not yet ready and its detail contains "warming" without "failed"; `false` otherwise.</returns>
    private static bool IsCapabilityActivelyWarming(
        ContainerizedProbeResult probeResult,
        ContainerCapabilityStage stage,
        AppSettings settings,
        string? providerId)
    {
        if (probeResult.State != ContainerizedProbeState.Available || probeResult.Capabilities is null)
            return false;

        if (IsStageReadyForSelection(settings, probeResult.Capabilities, stage, providerId, out var detail))
            return false;

        // Retry only for active warmup (e.g. "Qwen3-TTS warming up").
        // Do NOT retry for terminal failures ("warmup failed") or probe timeouts
        // ("Capabilities probe is still warming or failed") — both contain "failed".
        return detail is not null
            && detail.Contains("warming", StringComparison.OrdinalIgnoreCase)
            && !detail.Contains("failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Map a container probe result to a ProviderReadiness describing whether the requested capability stage is available.
    /// </summary>
    /// <param name="settings">Application settings used to determine host labeling and provider defaults.</param>
    /// <param name="probeResult">Probe information including service URL, state, capabilities, and any error/detail text.</param>
    /// <param name="stage">The capability stage to evaluate (Transcription, Translation, Tts, or Diarization).</param>
    /// <param name="providerId">Optional provider identifier to evaluate provider-specific readiness for TTS or Diarization; when null or empty the corresponding default from <paramref name="settings"/> is used.</param>
    /// <returns>A <see cref="ProviderReadiness"/> indicating readiness; when not ready the returned object contains a user-facing message explaining the reason.</returns>
    internal static ProviderReadiness MapProbeResultToReadiness(
        AppSettings settings,
        ContainerizedProbeResult probeResult,
        ContainerCapabilityStage stage,
        string? providerId = null)
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
            var unreachableDetail = string.IsNullOrWhiteSpace(probeResult.ErrorDetail)
                ? probeResult.ServiceUrl
                : $"{probeResult.ServiceUrl} ({probeResult.ErrorDetail})";
            return new ProviderReadiness(false, BuildUnreachableMessage(settings, probeResult.ServiceUrl, unreachableDetail));
        }

        string? detail = null;
        if (probeResult.Capabilities is null || !IsStageReadyForSelection(settings, probeResult.Capabilities, stage, providerId, out detail))
        {
            var stageLabel = stage switch
            {
                ContainerCapabilityStage.Transcription => "transcription",
                ContainerCapabilityStage.Translation => "translation",
                ContainerCapabilityStage.Tts => "TTS",
                _ => "diarization",
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

    /// <summary>
    /// Determines whether the requested capability stage is ready, optionally scoped to a specific provider for TTS or Diarization.
    /// </summary>
    /// <param name="settings">Application settings used to select a default provider when <paramref name="providerId"/> is not provided.</param>
    /// <param name="capabilities">Snapshot of container capabilities and provider-specific readiness information.</param>
    /// <param name="stage">The capability stage to check (e.g., Transcription, Translation, Tts, Diarization).</param>
    /// <param name="providerId">Optional provider identifier; when null or whitespace, the corresponding provider from <paramref name="settings"/> is used for TTS and Diarization.</param>
    /// <param name="detail">Output detail string describing the capability or provider readiness state; provider-specific detail overrides the generic stage detail when available.</param>
    /// <returns>`true` if the stage (or the selected provider for TTS/Diarization) is ready, `false` otherwise.</returns>
    private static bool IsStageReadyForSelection(
        AppSettings settings,
        ContainerCapabilitiesSnapshot capabilities,
        ContainerCapabilityStage stage,
        string? providerId,
        out string? detail)
    {
        detail = capabilities.Detail(stage);

        if (stage == ContainerCapabilityStage.Tts)
        {
            var ttsProviderId = string.IsNullOrWhiteSpace(providerId) ? settings.TtsProvider : providerId;
            if (string.IsNullOrWhiteSpace(ttsProviderId))
            {
                detail = "TTS provider is not advertised by host.";
                return false;
            }

            if (!capabilities.TryGetTtsProviderReadiness(ttsProviderId, out var providerReady, out var providerDetail))
            {
                detail = $"TTS provider '{ttsProviderId}' is not advertised by host.";
                return false;
            }

            detail = string.IsNullOrWhiteSpace(providerDetail) ? detail : providerDetail;
            return providerReady;
        }

        if (stage == ContainerCapabilityStage.Diarization)
        {
            var diarizationProviderId = string.IsNullOrWhiteSpace(providerId) ? settings.DiarizationProvider : providerId;
            if (string.IsNullOrWhiteSpace(diarizationProviderId))
            {
                detail = "Diarization provider is not advertised by host.";
                return false;
            }

            if (!capabilities.TryGetDiarizationProviderReadiness(diarizationProviderId, out var providerReady, out var providerDetail))
            {
                detail = $"Diarization provider '{diarizationProviderId}' is not advertised by host.";
                return false;
            }

            detail = string.IsNullOrWhiteSpace(providerDetail) ? detail : providerDetail;
            return providerReady;
        }

        return capabilities.IsReady(stage);
    }
}
