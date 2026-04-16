using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
    private static readonly string DebugLogPath = ResolveDebugLogPath();

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
        ApiKeyStore? keyStore = null,
        ContainerizedServiceProbe? serviceProbe = null) =>
        Check(settings, ContainerCapabilityStage.Transcription, serviceProbe);

    public static ProviderReadiness CheckTranslation(
        AppSettings settings,
        ApiKeyStore? keyStore = null,
        ContainerizedServiceProbe? serviceProbe = null) =>
        Check(settings, ContainerCapabilityStage.Translation, serviceProbe);

    public static ProviderReadiness CheckVocalSeparation(
        AppSettings settings,
        ApiKeyStore? keyStore = null,
        ContainerizedServiceProbe? serviceProbe = null) =>
        Check(settings, ContainerCapabilityStage.VocalSeparation, serviceProbe);

    /// <summary>
    /// Determines whether the configured containerized GPU host is ready to perform TTS (text-to-speech) operations.
    /// </summary>
    public static ProviderReadiness CheckTts(
        AppSettings settings,
        ApiKeyStore? keyStore = null,
        ContainerizedServiceProbe? serviceProbe = null) =>
        Check(settings, ContainerCapabilityStage.Tts, serviceProbe);

    /// <summary>
    /// Determines whether the configured containerized GPU host exposes a ready diarization capability.
    /// </summary>
    public static ProviderReadiness CheckDiarization(
        AppSettings settings,
        string providerId,
        ApiKeyStore? keyStore = null,
        ContainerizedServiceProbe? serviceProbe = null) =>
        Check(settings, ContainerCapabilityStage.Diarization, serviceProbe, providerId);

    /// <summary>
    /// Waits for the containerized transcription capability to become ready for execution.
    /// </summary>
    public static Task<ProviderReadiness> CheckTranscriptionForExecutionAsync(
        AppSettings settings,
        ContainerizedServiceProbe serviceProbe,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Transcription, serviceProbe, cancellationToken, waitOptions: ExecutionWaitOptions.Default);

    public static Task<ProviderReadiness> CheckTranslationForExecutionAsync(
        AppSettings settings,
        ContainerizedServiceProbe serviceProbe,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Translation, serviceProbe, cancellationToken, waitOptions: ExecutionWaitOptions.Default);

    public static Task<ProviderReadiness> CheckVocalSeparationForExecutionAsync(
        AppSettings settings,
        ContainerizedServiceProbe serviceProbe,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.VocalSeparation, serviceProbe, cancellationToken, waitOptions: ExecutionWaitOptions.Default);

    public static Task<ProviderReadiness> CheckTtsForExecutionAsync(
        AppSettings settings,
        ContainerizedServiceProbe serviceProbe,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Tts, serviceProbe, cancellationToken, waitOptions: ExecutionWaitOptions.Default);

    public static Task<ProviderReadiness> CheckDiarizationForExecutionAsync(
        AppSettings settings,
        string providerId,
        ContainerizedServiceProbe serviceProbe,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Diarization, serviceProbe, cancellationToken, providerId, ExecutionWaitOptions.Default);

    internal static Task<ProviderReadiness> CheckDiarizationForExecutionAsync(
        AppSettings settings,
        string providerId,
        ContainerizedServiceProbe serviceProbe,
        ExecutionWaitOptions waitOptions,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Diarization, serviceProbe, cancellationToken, providerId, waitOptions);

    internal static Task<ProviderReadiness> CheckTtsForExecutionAsync(
        AppSettings settings,
        ContainerizedServiceProbe serviceProbe,
        ExecutionWaitOptions waitOptions,
        CancellationToken cancellationToken = default) =>
        CheckForExecutionAsync(settings, ContainerCapabilityStage.Tts, serviceProbe, cancellationToken, waitOptions: waitOptions);

    private static ProviderReadiness Check(
        AppSettings settings,
        ContainerCapabilityStage stage,
        ContainerizedServiceProbe? serviceProbe,
        string? providerId = null)
    {
        var serviceUrl = settings.EffectiveGpuServiceUrl;
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return new ProviderReadiness(false, "No GPU inference host URL configured.");

        var probeResult = serviceProbe?.GetCurrentOrStartBackgroundProbe(serviceUrl)
            ?? FromHealth(ContainerizedInferenceClient.CheckHealth(serviceUrl, timeoutSeconds: 2));

        return MapProbeResultToReadiness(settings, probeResult, stage, providerId);
    }

    private static async Task<ProviderReadiness> CheckForExecutionAsync(
        AppSettings settings,
        ContainerCapabilityStage stage,
        ContainerizedServiceProbe serviceProbe,
        CancellationToken cancellationToken,
        string? providerId = null,
        ExecutionWaitOptions waitOptions = default)
    {
        waitOptions = waitOptions == default ? ExecutionWaitOptions.Default : waitOptions;

        var serviceUrl = settings.EffectiveGpuServiceUrl;
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return new ProviderReadiness(false, "No GPU inference host URL configured.");

        var probeResult = await serviceProbe.WaitForProbeAsync(
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
                probeResult = await serviceProbe.WaitForProbeAsync(
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

    private static bool IsCapabilityActivelyWarming(
        ContainerizedProbeResult probeResult,
        ContainerCapabilityStage stage,
        AppSettings settings,
        string? providerId)
    {
        if (probeResult.State != ContainerizedProbeState.Available)
            return false;

        if (probeResult.Capabilities is null)
            return !string.IsNullOrWhiteSpace(probeResult.CapabilitiesError);

        if (IsStageReadyForSelection(settings, probeResult.Capabilities, stage, providerId, out var detail))
            return false;

        return detail is not null
            && !detail.Contains("failed", StringComparison.OrdinalIgnoreCase)
            && (detail.Contains("warming", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("in progress", StringComparison.OrdinalIgnoreCase));
    }

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

        if (probeResult.Capabilities is null)
        {
            return new ProviderReadiness(
                false,
                BuildCapabilitiesUnavailableMessage(hostLabel, stage, probeResult.CapabilitiesError, probeResult.IsStale));
        }

        if (!IsStageReadyForSelection(settings, probeResult.Capabilities, stage, providerId, out var detail))
        {
            var stageLabel = stage switch
            {
                ContainerCapabilityStage.Transcription => "transcription",
                ContainerCapabilityStage.Translation => "translation",
                ContainerCapabilityStage.Tts => "TTS",
                ContainerCapabilityStage.VocalSeparation => "vocal separation",
                _ => "diarization",
            };
            return new ProviderReadiness(
                false,
                BuildCapabilityNotReadyMessage(hostLabel, stageLabel, detail, probeResult.IsStale));
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
            health.Capabilities,
            health.CapabilitiesError);

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

    private static string BuildCapabilityNotReadyMessage(string hostLabel, string stageLabel, string? detail, bool isStale)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return isStale
                ? $"{hostLabel} cached status indicates {stageLabel} capability is unavailable."
                : $"{hostLabel} is live but {stageLabel} capability is unavailable.";
        }

        if (isStale)
            return $"{hostLabel} cached status indicates {stageLabel} capability is unavailable: {detail}";

        if (IsActualWarmupDetail(detail))
            return $"{hostLabel} is live but {stageLabel} capability is warming: {detail}";

        if (detail.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return $"{hostLabel} is live but {stageLabel} capability failed: {detail}";

        return $"{hostLabel} is live but {stageLabel} capability is unavailable: {detail}";
    }

    private static string BuildCapabilitiesUnavailableMessage(string hostLabel, ContainerCapabilityStage stage, string? detail, bool isStale)
    {
        var stageLabel = stage switch
        {
            ContainerCapabilityStage.Transcription => "transcription",
            ContainerCapabilityStage.Translation => "translation",
            ContainerCapabilityStage.Tts => "TTS",
            ContainerCapabilityStage.VocalSeparation => "vocal separation",
            _ => "diarization",
        };

        if (string.IsNullOrWhiteSpace(detail))
        {
            return isStale
                ? $"{hostLabel} cached status indicates {stageLabel} capability metadata is unavailable."
                : $"{hostLabel} is live but {stageLabel} capability metadata is unavailable.";
        }

        return isStale
            ? $"{hostLabel} cached status indicates {stageLabel} capability metadata could not be read: {detail}"
            : $"{hostLabel} is live but {stageLabel} capability metadata could not be read: {detail}";
    }

    private static bool IsActualWarmupDetail(string detail) =>
        detail.Contains("warming", StringComparison.OrdinalIgnoreCase)
        && !detail.Contains("failed", StringComparison.OrdinalIgnoreCase)
        && !detail.Contains("probe", StringComparison.OrdinalIgnoreCase)
        && !detail.Contains("cached", StringComparison.OrdinalIgnoreCase);

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

    private static void WriteDebugLog(string runId, string hypothesisId, string location, string message, object data)
    {
        var payload = new
        {
            sessionId = "f76224",
            runId,
            hypothesisId,
            location,
            message,
            data,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        try
        {
            var line = JsonSerializer.Serialize(payload);
            File.AppendAllText(DebugLogPath, line + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static string ResolveDebugLogPath()
    {
        var envPath = Environment.GetEnvironmentVariable("BABEL_DEBUG_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return envPath;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Babel-Player.sln")))
                return Path.Combine(dir.FullName, "debug-f76224.log");
            dir = dir.Parent;
        }

        return Path.Combine(Environment.CurrentDirectory, "debug-f76224.log");
    }
}
