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
            CurrentSettings.TranscriptionRuntime);

    private ITranslationProvider CreateTranslationService() =>
        TranslationRegistry.CreateProvider(
            CurrentSettings.TranslationProvider,
            CurrentSettings,
            KeyStore,
            CurrentSettings.TranslationRuntime);

    private ITtsProvider CreateTtsService() =>
        TtsRegistry.CreateProvider(
            CurrentSettings.TtsProvider,
            CurrentSettings,
            KeyStore,
            CurrentSettings.TtsRuntime);

    private void RequestContainerizedAutostartForSettings() =>
        _containerizedInferenceManager?.RequestEnsureStarted(CurrentSettings, ContainerizedStartupTrigger.SettingsChanged);

    private Task EnsureContainerizedExecutionRuntimeStartedAsync(
        InferenceRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        if (runtime != InferenceRuntime.Containerized || _containerizedInferenceManager is null)
            return Task.CompletedTask;

        return _containerizedInferenceManager.EnsureStartedAsync(
            CurrentSettings,
            ContainerizedStartupTrigger.Execution,
            cancellationToken);
    }
}
