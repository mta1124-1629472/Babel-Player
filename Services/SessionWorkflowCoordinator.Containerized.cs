using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

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
}
