using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Diarization provider backed by the containerized NeMo endpoint.
/// </summary>
public sealed class NemoContainerizedDiarizationProvider : IDiarizationProvider
{
    private readonly ContainerizedInferenceClient _client;
    private readonly AppLog _log;
    private readonly ContainerizedServiceProbe? _probe;

    /// <summary>
    /// Initializes a new instance of <c>NemoContainerizedDiarizationProvider</c>.
    /// </summary>
    /// <param name="client">Client used to perform containerized diarization requests.</param>
    /// <param name="log">Application logger for informational and diagnostic messages.</param>
    /// <param name="probe">Optional probe used to check container or service readiness for execution.</param>
    public NemoContainerizedDiarizationProvider(
        ContainerizedInferenceClient client,
        AppLog log,
        ContainerizedServiceProbe? probe = null)
    {
        _client = client;
        _log = log;
        _probe = probe;
    }

    /// <summary>
    /// Determines the readiness of the Nemo containerized diarization provider using the provided application settings and optional API key store.
    /// </summary>
    /// <param name="settings">Application settings used to evaluate provider readiness.</param>
    /// <param name="keyStore">Optional API key store for provider authentication information.</param>
    /// <returns>A <see cref="ProviderReadiness"/> describing whether the provider is available and any configuration or authentication requirements.</returns>
    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore) =>
        ContainerizedProviderReadiness.CheckDiarization(settings, ProviderNames.NemoLocal, _probe, keyStore);

    /// <summary>
    /// Ensures the provider is ready to execute diarization requests against the containerized service.
    /// </summary>
    /// <param name="settings">Application settings used for readiness checks.</param>
    /// <param name="progress">If provided, reports progress value 1.0 immediately.</param>
    /// <param name="ct">Cancellation token to cancel the readiness check.</param>
    /// <returns>`true` if the provider is ready to execute diarization requests, `false` otherwise.</returns>
    public async Task<bool> EnsureReadyAsync(
        AppSettings settings,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (_probe is null)
        {
            var result = CheckReadiness(settings, null);
            progress?.Report(1.0);
            return result.IsReady;
        }

        var readiness = await ContainerizedProviderReadiness.CheckDiarizationForExecutionAsync(
            settings,
            ProviderNames.NemoLocal,
            _probe,
            ct).ConfigureAwait(false);
        progress?.Report(1.0);
        return readiness.IsReady;
    }

    /// <summary>
    /// Performs speaker diarization on the audio file specified by the request using the Nemo containerized model.
    /// </summary>
    /// <param name="request">Diarization request; <c>request.SourceAudioPath</c> must reference an existing audio file. </param>
    /// <param name="ct">Cancellation token to cancel the diarization operation.</param>
    /// <returns>A <see cref="DiarizationResult"/> containing speaker segments and related metadata for the provided audio.</returns>
    /// <exception cref="FileNotFoundException">Thrown when <c>request.SourceAudioPath</c> does not exist on disk.</exception>
    public async Task<DiarizationResult> DiarizeAsync(
        DiarizationRequest request,
        CancellationToken ct = default)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        _log.Info($"[NemoContainerizedDiarization] Diarizing: {request.SourceAudioPath}");
        return await _client.DiarizeAsync(
                request.SourceAudioPath,
                ProviderNames.NemoDiarizationAlias,
                request.MinSpeakers,
                request.MaxSpeakers,
                ct)
            .ConfigureAwait(false);
    }
}
