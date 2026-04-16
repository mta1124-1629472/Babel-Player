using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public sealed class ContainerizedVocalSeparationProvider : IVocalSeparationProvider
{
    private readonly ContainerizedInferenceClient _client;
    private readonly AppLog _log;

    public ContainerizedVocalSeparationProvider(ContainerizedInferenceClient client, AppLog log)
    {
        _client = client;
        _log = log;
    }

    public async Task<VocalSeparationResult> SeparateVocalsAsync(
        VocalSeparationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        if (!Directory.Exists(request.OutputDirectoryPath))
            Directory.CreateDirectory(request.OutputDirectoryPath);

        var result = await _client.SeparateVocalsAsync(
            request.SourceAudioPath,
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException(
                $"Containerized vocal separation failed: {result.ErrorMessage}");

        _log.Info($"[ContainerizedVocalSeparation] Complete: vocals='{result.VocalsAudioPath}', instrumental='{result.InstrumentalAudioPath}'");
        return result;
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
        ContainerizedProviderReadiness.CheckVocalSeparation(settings);
}
