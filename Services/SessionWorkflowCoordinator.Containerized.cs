using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Registries;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    private ITranscriptionProvider CreateTranscriptionService() =>
        TranscriptionRegistry.CreateProvider(
            CurrentSettings.TranscriptionProvider,
            CurrentSettings,
            KeyStore,
            CurrentSettings.TranscriptionProfile);

    private ITranslationProvider CreateTranslationService()
    {
        var provider = TranslationRegistry.CreateProvider(
            CurrentSettings.TranslationProvider,
            CurrentSettings,
            KeyStore,
            CurrentSettings.TranslationProfile);
        // Wrap CTranslate2 CPU provider so that if it fails at inference time the
        // pipeline automatically falls back to NLLB (PyTorch) and records a note.
        if (CurrentSettings.TranslationProvider == ProviderNames.CTranslate2
            && CurrentSettings.TranslationProfile == ComputeProfile.Cpu)
        {
            var fallback = TranslationRegistry.CreateProvider(
                ProviderNames.Nllb200,
                CurrentSettings,
                KeyStore,
                ComputeProfile.Cpu);
            return new CTranslate2FallbackTranslationProvider(provider, fallback, _log,
                note => TranslationFallbackNote = note);
        }
        return provider;
    }

    private ITtsProvider CreateTtsService() =>
        TtsRegistry.CreateProvider(
            CurrentSettings.TtsProvider,
            CurrentSettings,
            KeyStore,
            CurrentSettings.TtsProfile);

    private void RequestContainerizedAutostartForSettings()
    {
        if (!RequiresContainerizedRuntime())
        {
            RuntimeWarmupStatusText = null;
            return;
        }

        if (_containerizedInferenceManager is null)
            return;

        RuntimeWarmupStatusText = $"{GetConfiguredGpuHostLabel()} start requested…";
        BackgroundTaskObserver.Observe(
            EnsureContainerizedAutostartForSettingsAsync(),
            _log,
            "GPU runtime settings autostart");
    }

    private Task EnsureContainerizedExecutionRuntimeStartedAsync(
        InferenceRuntime runtime,
        CancellationToken cancellationToken = default) =>
        EnsureContainerizedExecutionRuntimeStartedAsync(runtime, null, cancellationToken);

    private async Task EnsureContainerizedExecutionRuntimeStartedAsync(
        InferenceRuntime runtime,
        string? stageLabel,
        CancellationToken cancellationToken = default)
    {
        if (runtime != InferenceRuntime.Containerized || _containerizedInferenceManager is null)
            return;

        RuntimeWarmupStatusText = string.IsNullOrWhiteSpace(stageLabel)
            ? $"{GetConfiguredGpuHostLabel()} is starting…"
            : $"{stageLabel}: {GetConfiguredGpuHostLabel().ToLowerInvariant()} is starting…";
        var result = await _containerizedInferenceManager.EnsureStartedAsync(
            CurrentSettings,
            ContainerizedStartupTrigger.Execution,
            cancellationToken);
        RuntimeWarmupStatusText = result.Message;
        await RefreshRuntimeWarmupStatusFromProbeAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);

        if (result.Attempted && !result.IsReady)
        {
            var prefix = string.IsNullOrWhiteSpace(stageLabel)
                ? "GPU inference host startup failed"
                : $"{stageLabel} GPU inference host startup failed";
            throw new PipelineProviderException($"{prefix}: {result.Message}");
        }
    }

    private async Task EnsureTranslationExecutionReadyAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureContainerizedExecutionRuntimeStartedAsync(
            CurrentSettings.TranslationRuntime,
            "Translation",
            cancellationToken);

        ProviderReadiness readiness;
        if (CurrentSettings.TranslationRuntime == InferenceRuntime.Containerized && _containerizedProbe is not null)
        {
            var probeResult = await _containerizedProbe.WaitForProbeAsync(
                CurrentSettings.EffectiveGpuServiceUrl,
                forceRefresh: true,
                cancellationToken: cancellationToken);

            var capabilityReady = probeResult.Capabilities?.IsReady(ContainerCapabilityStage.Translation) ?? false;
            var capabilityDetail = probeResult.Capabilities?.Detail(ContainerCapabilityStage.Translation) ?? "<none>";
            _log.Info(
                $"Translation GPU route: provider={CurrentSettings.TranslationProvider}, model={CurrentSettings.TranslationModel}, " +
                $"service_url={CurrentSettings.EffectiveGpuServiceUrl}, capability_ready={capabilityReady}, detail='{capabilityDetail}'");

            readiness = ContainerizedProviderReadiness.MapProbeResultToReadiness(
                CurrentSettings,
                probeResult,
                ContainerCapabilityStage.Translation);
            RuntimeWarmupStatusText = DescribeRuntimeWarmupStatus(probeResult);
        }
        else
        {
            _log.Info(
                $"Translation route: runtime={CurrentSettings.TranslationRuntime}, provider={CurrentSettings.TranslationProvider}, model={CurrentSettings.TranslationModel}");
            readiness = TranslationRegistry.CheckReadiness(
                CurrentSettings.TranslationProvider,
                CurrentSettings.TranslationModel,
                CurrentSettings,
                KeyStore,
                CurrentSettings.TranslationProfile);
        }

        if (!readiness.IsReady && !readiness.RequiresModelDownload)
            throw new PipelineProviderException(readiness.BlockingReason!);

        if (!readiness.RequiresModelDownload)
            return;

        if (!await TranslationRegistry.EnsureModelAsync(
                CurrentSettings.TranslationProvider,
                CurrentSettings.TranslationModel,
                CurrentSettings,
                progress,
                cancellationToken,
                CurrentSettings.TranslationProfile,
                KeyStore))
        {
            throw new InvalidOperationException($"Failed to download model '{CurrentSettings.TranslationModel}'.");
        }
    }

    private async Task EnsureContainerizedAutostartForSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_containerizedInferenceManager is null)
            return;

        var result = await _containerizedInferenceManager.EnsureStartedAsync(
            CurrentSettings,
            ContainerizedStartupTrigger.SettingsChanged,
            cancellationToken).ConfigureAwait(false);

        RuntimeWarmupStatusText = result.Message;
        await RefreshRuntimeWarmupStatusFromProbeAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshRuntimeWarmupStatusFromProbeAsync(
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        if (_containerizedProbe is null || !RequiresContainerizedRuntime())
            return;

        var probeResult = await _containerizedProbe.WaitForProbeAsync(
            CurrentSettings.EffectiveGpuServiceUrl,
            forceRefresh: forceRefresh,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        RuntimeWarmupStatusText = DescribeRuntimeWarmupStatus(probeResult);
    }

    private string? DescribeRuntimeWarmupStatus(ContainerizedProbeResult probeResult)
    {
        var hostLabel = GetConfiguredGpuHostLabel();
        if (probeResult.State == ContainerizedProbeState.Checking)
            return $"{hostLabel} is starting…";

        if (probeResult.State == ContainerizedProbeState.Unavailable)
        {
            return string.IsNullOrWhiteSpace(probeResult.ErrorDetail)
                ? $"{hostLabel} is unavailable."
                : $"{hostLabel} is unavailable: {probeResult.ErrorDetail}";
        }

        if (probeResult.IsStale)
            return $"{hostLabel} status is cached while a fresh probe is running.";

        var providerWarmup = FindActiveWarmupDetail(probeResult);
        if (!string.IsNullOrWhiteSpace(providerWarmup))
            return providerWarmup;

        if (probeResult.Busy && !string.IsNullOrWhiteSpace(probeResult.BusyReason))
            return $"{hostLabel} is busy: {probeResult.BusyReason}";

        return $"{hostLabel} is ready.";
    }

    private string? FindActiveWarmupDetail(ContainerizedProbeResult probeResult)
    {
        foreach (var snapshot in EnumerateProviderHealth(probeResult))
        {
            var state = snapshot?.State;
            if (snapshot is null
                || string.IsNullOrWhiteSpace(snapshot.Detail)
                || (!string.Equals(state, "warming", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(state, "refreshing", StringComparison.OrdinalIgnoreCase))
                || snapshot.Detail.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return snapshot.Detail;
        }

        return null;
    }

    private static IEnumerable<ContainerProviderHealthSnapshot?> EnumerateProviderHealth(ContainerizedProbeResult probeResult)
    {
        if (probeResult.ProviderHealth is not null)
        {
            foreach (var snapshot in probeResult.ProviderHealth.Values)
                yield return snapshot;
        }

        if (probeResult.Capabilities?.TtsProviderHealth is not null)
        {
            foreach (var snapshot in probeResult.Capabilities.TtsProviderHealth.Values)
                yield return snapshot;
        }

        if (probeResult.Capabilities?.DiarizationProviderHealth is not null)
        {
            foreach (var snapshot in probeResult.Capabilities.DiarizationProviderHealth.Values)
                yield return snapshot;
        }
    }

    private bool RequiresContainerizedRuntime() =>
        CurrentSettings.TranscriptionRuntime == InferenceRuntime.Containerized
        || CurrentSettings.TranslationRuntime == InferenceRuntime.Containerized
        || CurrentSettings.TtsRuntime == InferenceRuntime.Containerized;

    private string GetConfiguredGpuHostLabel() =>
        CurrentSettings.PreferredLocalGpuBackend == GpuHostBackend.ManagedVenv
            ? "Managed local GPU host"
            : "Docker GPU host";
}
