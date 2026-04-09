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
/// Diarization provider backed by the containerized WeSpeaker CPU fallback endpoint.
/// </summary>
public sealed class WeSpeakerContainerizedDiarizationProvider : IDiarizationProvider
{
    private readonly ContainerizedInferenceClient _client;
    private readonly AppLog _log;
    private readonly ContainerizedServiceProbe? _probe;

    /// <summary>
    /// Initializes a new instance of <see cref="WeSpeakerContainerizedDiarizationProvider"/>.
    /// </summary>
    /// <param name="client">Containerized inference client used to perform diarization requests.</param>
    /// <param name="log">Application log used for informational logging.</param>
    /// <param name="probe">Optional containerized service probe used to check service readiness for execution.</param>
    public WeSpeakerContainerizedDiarizationProvider(
        ContainerizedInferenceClient client,
        AppLog log,
        ContainerizedServiceProbe? probe = null)
    {
        _client = client;
        _log = log;
        _probe = probe;
    }

    /// <summary>
    /// Determines the readiness of the WeSpeaker containerized diarization provider for the given application settings.
    /// </summary>
    /// <param name="settings">Application settings used to evaluate provider configuration and requirements.</param>
    /// <param name="keyStore">Optional API key store used to validate any required credentials.</param>
    /// <returns>A <see cref="ProviderReadiness"/> describing availability and any unmet requirements for executing diarization.</returns>
    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore) =>
        ContainerizedProviderReadiness.CheckDiarization(settings, ProviderNames.WeSpeakerLocal, _probe, keyStore);

    /// <summary>
    /// Ensures the provider is ready to execute diarization and reports the readiness result.
    /// </summary>
    /// <param name="settings">Application settings used to evaluate provider readiness.</param>
    /// <param name="progress">If non-null, reports progress immediately as 1.0 before performing any probe.</param>
    /// <param name="ct">Cancellation token used for the readiness probe.</param>
    /// <returns>`true` if the provider is ready to execute diarization, `false` otherwise.</returns>
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
            ProviderNames.WeSpeakerLocal,
            _probe,
            ct).ConfigureAwait(false);
        progress?.Report(1.0);
        return readiness.IsReady;
    }

    /// <summary>
    /// Performs speaker diarization on the given audio file using the WeSpeaker containerized model.
    /// </summary>
    /// <param name="request">Diarization parameters including the source audio path and min/max speaker bounds.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    /// <returns>A <see cref="DiarizationResult"/> containing speaker segments and related metadata.</returns>
    /// <exception cref="FileNotFoundException">Thrown when <see cref="DiarizationRequest.SourceAudioPath"/> does not point to an existing file.</exception>
    public async Task<DiarizationResult> DiarizeAsync(
        DiarizationRequest request,
        CancellationToken ct = default)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        _log.Info($"[WeSpeakerContainerizedDiarization] Diarizing: {request.SourceAudioPath}");
        return await _client.DiarizeAsync(
                request.SourceAudioPath,
                ProviderNames.WeSpeakerDiarizationAlias,
                request.MinSpeakers,
                request.MaxSpeakers,
                ct)
            .ConfigureAwait(false);
    }
}
