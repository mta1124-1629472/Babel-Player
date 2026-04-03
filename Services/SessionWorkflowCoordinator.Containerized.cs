using System;
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

    private ITranslationProvider CreateTranslationService() =>
        TranslationRegistry.CreateProvider(
            CurrentSettings.TranslationProvider,
            CurrentSettings,
            KeyStore,
            CurrentSettings.TranslationProfile);

    private ITtsProvider CreateTtsService() =>
        TtsRegistry.CreateProvider(
            CurrentSettings.TtsProvider,
            CurrentSettings,
            KeyStore,
            CurrentSettings.TtsProfile);

    private void RequestContainerizedAutostartForSettings() =>
        _containerizedInferenceManager?.RequestEnsureStarted(CurrentSettings, ContainerizedStartupTrigger.SettingsChanged);

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

        var result = await _containerizedInferenceManager.EnsureStartedAsync(
            CurrentSettings,
            ContainerizedStartupTrigger.Execution,
            cancellationToken);

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
}
