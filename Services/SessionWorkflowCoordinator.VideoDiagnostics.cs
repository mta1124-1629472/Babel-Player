using System;
using System.Collections.Generic;
using System.Linq;
using Babel.Player.Models;
using Babel.Player.Services.Registries;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    private void RefreshVideoEnhancementDiagnostics()
    {
        var diagnostics = VideoEnhancementDiagnostics.Create(
            CurrentSettings,
            HardwareSnapshot,
            _latestVsrDiagnostic);

        if (VideoEnhancementDiagnostics == diagnostics)
            return;

        VideoEnhancementDiagnostics = diagnostics;
        _log.Debug(
            $"Video enhancement diagnostics updated: support_hint='{diagnostics.SupportHintText}', " +
            $"requested='{diagnostics.RequestedStateText}', resolved='{diagnostics.ResolvedStateText}', " +
            $"backend='{diagnostics.BackendSummaryText}'");
    }

    private void EnsureSourcePlayerDiagnosticsSubscribed(IMediaTransport player)
    {
        if (_subscribedToSourceDiagnostics || player is not LibMpvEmbeddedTransport embedded)
            return;

        embedded.VsrDiagnosticChanged += _vsrDiagnosticChangedHandler;
        _subscribedToSourceDiagnostics = true;

        if (embedded.LastVsrDiagnostic is not null)
            RecordVsrDiagnosticSnapshot(embedded.LastVsrDiagnostic);
    }

    internal void RecordVsrDiagnosticSnapshot(VsrDiagnosticSnapshot snapshot)
    {
        _latestVsrDiagnostic = snapshot;
        RefreshVideoEnhancementDiagnostics();
    }

    partial void OnHardwareSnapshotChanged(HardwareSnapshot value)
    {
        RefreshVideoEnhancementDiagnostics();
        CoerceUnsupportedGpuSelections(value);
        EmitReadinessSignal(
            ReadinessSignalKind.BootstrapApplied,
            summary: "Hardware snapshot updated.",
            source: nameof(HardwareSnapshot),
            forceRefresh: true);
    }

    private void CoerceUnsupportedGpuSelections(HardwareSnapshot hardwareSnapshot)
    {
        if (hardwareSnapshot.IsDetecting || hardwareSnapshot.HasCuda)
            return;

        if (CurrentSettings.TranscriptionProfile != ComputeProfile.Gpu
            && CurrentSettings.TranslationProfile != ComputeProfile.Gpu
            && CurrentSettings.TtsProfile != ComputeProfile.Gpu)
        {
            return;
        }

        var selection = CreateCpuFallbackSelectionForUnavailableGpu();
        if (!ApplyPipelineSelectionSettings(selection))
            return;

        RequestContainerizedAutostartForSettings();
        NotifySettingsModified();
        _log.Warning(
            "CUDA is unavailable on this machine. GPU pipeline selections were switched to CPU-compatible providers and models without resetting the current session.");
    }

    private PipelineSettingsSelection CreateCpuFallbackSelectionForUnavailableGpu()
    {
        var transcriptionRuntime = CurrentSettings.TranscriptionProfile == ComputeProfile.Gpu
            ? ComputeProfile.Cpu
            : CurrentSettings.TranscriptionProfile;
        var translationRuntime = CurrentSettings.TranslationProfile == ComputeProfile.Gpu
            ? ComputeProfile.Cpu
            : CurrentSettings.TranslationProfile;
        var ttsRuntime = CurrentSettings.TtsProfile == ComputeProfile.Gpu
            ? ComputeProfile.Cpu
            : CurrentSettings.TtsProfile;

        var transcriptionProvider = ResolveProviderForRuntime(
            TranscriptionRegistry.GetAvailableProviders(transcriptionRuntime),
            InferenceRuntimeCatalog.NormalizeTranscriptionProvider(transcriptionRuntime, CurrentSettings.TranscriptionProvider));
        var translationProvider = ResolveProviderForRuntime(
            TranslationRegistry.GetAvailableProviders(translationRuntime),
            InferenceRuntimeCatalog.NormalizeTranslationProvider(translationRuntime, CurrentSettings.TranslationProvider));
        var ttsProvider = ResolveProviderForRuntime(
            TtsRegistry.GetAvailableProviders(ttsRuntime),
            InferenceRuntimeCatalog.NormalizeTtsProvider(ttsRuntime, CurrentSettings.TtsProvider));

        return new PipelineSettingsSelection(
            transcriptionRuntime,
            transcriptionProvider,
            ResolveModelId(
                TranscriptionRegistry.GetAvailableModels(transcriptionProvider, transcriptionRuntime, CurrentSettings),
                CurrentSettings.TranscriptionModel),
            translationRuntime,
            translationProvider,
            ResolveModelId(
                TranslationRegistry.GetAvailableModels(translationProvider, translationRuntime, CurrentSettings),
                CurrentSettings.TranslationModel),
            ttsRuntime,
            ttsProvider,
            ResolveModelId(
                TtsRegistry.GetAvailableModels(ttsProvider, ttsRuntime, CurrentSettings),
                CurrentSettings.TtsVoice),
            CurrentSettings.TargetLanguage,
            CurrentSettings.TranscriptionLanguageHint);
    }

    private static string ResolveProviderForRuntime(
        IReadOnlyList<ProviderDescriptor> providers,
        string normalizedProvider)
    {
        if (providers.Count == 0)
        {
            throw new InvalidOperationException(
                $"No providers are available for the requested runtime; cannot resolve '{normalizedProvider}'.");
        }

        if (providers.Any(provider => string.Equals(provider.Id, normalizedProvider, StringComparison.Ordinal)))
            return normalizedProvider;

        return providers[0].Id;
    }

    private static string ResolveModelId(IReadOnlyList<string> supportedModels, string? preferredModel)
    {
        if (supportedModels.Count == 0)
            return "default";

        if (!string.IsNullOrWhiteSpace(preferredModel)
            && supportedModels.Contains(preferredModel, StringComparer.Ordinal))
        {
            return preferredModel;
        }

        return supportedModels[0];
    }
}
