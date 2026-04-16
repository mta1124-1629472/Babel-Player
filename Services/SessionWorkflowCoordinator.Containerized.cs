using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
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

    private IVocalSeparationProvider CreateVocalSeparationProvider()
    {
        _vocalSeparationProvider ??= new ContainerizedVocalSeparationProvider(
            new ContainerizedInferenceClient(
                CurrentSettings.EffectiveContainerizedServiceUrl,
                _log,
                null,
                _requestLeaseTracker),
            _log);
        return _vocalSeparationProvider;
    }

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
            cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);

        ProviderReadiness readiness;
        if (CurrentSettings.TranslationRuntime == InferenceRuntime.Containerized && _containerizedProbe is not null)
        {
            var probeResult = await _containerizedProbe.WaitForProbeAsync(
                CurrentSettings.EffectiveGpuServiceUrl,
                forceRefresh: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

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
                KeyStore).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Failed to download model '{CurrentSettings.TranslationModel}'.");
        }
    }

    private async Task EnsureContainerizedAutostartForSettingsAsync(CancellationToken cancellationToken = default)
    {
        var manager = _containerizedInferenceManager;
        if (manager is null)
            return;

        var expectedServiceUrl = CurrentSettings.EffectiveGpuServiceUrl;
        var expectedRequiresContainerized = RequiresContainerizedRuntime();
        if (!expectedRequiresContainerized)
            return;

        var result = await manager.EnsureStartedAsync(
            CurrentSettings,
            ContainerizedStartupTrigger.SettingsChanged,
            cancellationToken).ConfigureAwait(false);

        if (!ReferenceEquals(manager, _containerizedInferenceManager)
            || !RequiresContainerizedRuntime()
            || !string.Equals(CurrentSettings.EffectiveGpuServiceUrl, expectedServiceUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var message = result.Message;
        Dispatcher.UIThread.Post(() => RuntimeWarmupStatusText = message);
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
        var statusText = DescribeRuntimeWarmupStatus(probeResult);
        Dispatcher.UIThread.Post(() => RuntimeWarmupStatusText = statusText);
    }

    private string? DescribeRuntimeWarmupStatus(ContainerizedProbeResult probeResult)
    {
        var hostLabel = GetConfiguredGpuHostLabel();
        string message;
        if (probeResult.State == ContainerizedProbeState.Checking)
            message = $"{hostLabel} is starting…";
        else if (probeResult.State == ContainerizedProbeState.Unavailable)
        {
            message = string.IsNullOrWhiteSpace(probeResult.ErrorDetail)
                ? $"{hostLabel} is unavailable."
                : $"{hostLabel} is unavailable: {probeResult.ErrorDetail}";
        }
        else if (probeResult.IsStale)
            message = $"{hostLabel} status is cached while a fresh probe is running.";
        else
        {
            var providerWarmup = FindActiveWarmupDetail(probeResult);
            if (!string.IsNullOrWhiteSpace(providerWarmup))
                message = providerWarmup;
            else if (probeResult.Busy && !string.IsNullOrWhiteSpace(probeResult.BusyReason))
                message = $"{hostLabel} is busy: {probeResult.BusyReason}";
            else
                message = $"{hostLabel} is ready.";
        }

        return AppendWarmupExpectationHint(probeResult, message);
    }

    private string AppendWarmupExpectationHint(ContainerizedProbeResult probeResult, string message)
    {
        if (probeResult.State == ContainerizedProbeState.Available
            && !probeResult.IsStale
            && string.IsNullOrWhiteSpace(FindActiveWarmupDetail(probeResult))
            && !probeResult.Busy)
        {
            return message;
        }

        if (probeResult.State == ContainerizedProbeState.Unavailable)
        {
            var coldBudget = TimeSpan.FromSeconds(90);
            if (DateTimeOffset.UtcNow - ProcessStartedAtUtc >= coldBudget)
                return message;
        }

        const string hint = " Typical first warm-up after launch or install: 30–60 seconds.";
        return message.EndsWith(hint, StringComparison.Ordinal) ? message : message + hint;
    }

    private string? FindActiveWarmupDetail(ContainerizedProbeResult probeResult)
    {
        foreach (var (providerKey, snapshot) in EnumerateKeyedProviderHealth(probeResult))
        {
            if (!IsProviderWarmupRelevantToCurrentSelection(providerKey))
                continue;

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

    /// <summary>
    /// The GPU host refreshes background provider health for NeMo and Qwen even when the user
    /// selected WeSpeaker diarization or Edge TTS. Only surface warmup text for providers that
    /// match the active pipeline selection.
    /// </summary>
    private bool IsProviderWarmupRelevantToCurrentSelection(string providerKey)
    {
        if (providerKey.StartsWith("tts:", StringComparison.OrdinalIgnoreCase))
        {
            var id = providerKey[4..];
            if (CurrentSettings.TtsRuntime != InferenceRuntime.Containerized)
                return false;

            var selected = InferenceRuntimeCatalog.NormalizeTtsProvider(
                CurrentSettings.TtsProfile,
                CurrentSettings.TtsProvider);
            var probeId = InferenceRuntimeCatalog.NormalizeTtsProvider(CurrentSettings.TtsProfile, id);
            return string.Equals(selected, probeId, StringComparison.Ordinal);
        }

        if (providerKey.StartsWith("diar:", StringComparison.OrdinalIgnoreCase))
        {
            var id = providerKey[5..];
            if (InferenceRuntimeCatalog.InferDiarizationRuntime(CurrentSettings.DiarizationProvider)
                != InferenceRuntime.Containerized)
                return false;

            var selected = InferenceRuntimeCatalog.NormalizeDiarizationCapabilityProviderId(
                CurrentSettings.DiarizationProvider);
            var probeId = InferenceRuntimeCatalog.NormalizeDiarizationCapabilityProviderId(id);
            return string.Equals(selected, probeId, StringComparison.Ordinal);
        }

        if (string.Equals(providerKey, "nemo", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(CurrentSettings.DiarizationProvider))
                return false;

            if (InferenceRuntimeCatalog.InferDiarizationRuntime(CurrentSettings.DiarizationProvider)
                != InferenceRuntime.Containerized)
                return false;

            return string.Equals(
                InferenceRuntimeCatalog.NormalizeDiarizationProvider(CurrentSettings.DiarizationProvider),
                ProviderNames.NemoLocal,
                StringComparison.Ordinal);
        }

        if (string.Equals(providerKey, "qwen", StringComparison.OrdinalIgnoreCase))
        {
            if (CurrentSettings.TtsRuntime != InferenceRuntime.Containerized)
                return false;

            return string.Equals(
                InferenceRuntimeCatalog.NormalizeTtsProvider(CurrentSettings.TtsProfile, CurrentSettings.TtsProvider),
                ProviderNames.Qwen,
                StringComparison.Ordinal);
        }

        return true;
    }

    private static IEnumerable<(string ProviderKey, ContainerProviderHealthSnapshot Snapshot)> EnumerateKeyedProviderHealth(
        ContainerizedProbeResult probeResult)
    {
        if (probeResult.ProviderHealth is not null)
        {
            foreach (var kv in probeResult.ProviderHealth)
                yield return (kv.Key, kv.Value);
        }

        if (probeResult.Capabilities?.TtsProviderHealth is not null)
        {
            foreach (var kv in probeResult.Capabilities.TtsProviderHealth)
                yield return ($"tts:{kv.Key}", kv.Value);
        }

        if (probeResult.Capabilities?.DiarizationProviderHealth is not null)
        {
            foreach (var kv in probeResult.Capabilities.DiarizationProviderHealth)
                yield return ($"diar:{kv.Key}", kv.Value);
        }
    }

    private bool RequiresContainerizedRuntime() =>
        CurrentSettings.TranscriptionRuntime == InferenceRuntime.Containerized
        || CurrentSettings.TranslationRuntime == InferenceRuntime.Containerized
        || CurrentSettings.TtsRuntime == InferenceRuntime.Containerized
        || InferenceRuntimeCatalog.InferDiarizationRuntime(CurrentSettings.DiarizationProvider) == InferenceRuntime.Containerized;

    private string GetConfiguredGpuHostLabel() =>
        CurrentSettings.PreferredLocalGpuBackend == GpuHostBackend.ManagedVenv
            ? "Managed local GPU host"
            : "Docker GPU host";
}
