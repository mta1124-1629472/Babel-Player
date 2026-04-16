using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Transcription provider backed by the Parakeet TDT model running in the containerized
/// inference service (<c>/transcribe/parakeet</c>). English-only; returns timed segments
/// in the same canonical format as <see cref="ContainerizedTranscriptionProvider"/>.
/// </summary>
public sealed class ParakeetTranscriptionProvider : ITranscriptionProvider
{
    private readonly ContainerizedInferenceClient _client;
    private readonly AppLog _log;

    public ParakeetTranscriptionProvider(ContainerizedInferenceClient client, AppLog log)
    {
        _client = client;
        _log = log;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        _log.Info($"[ParakeetTranscription] Transcribing: {request.SourceAudioPath}");

        var result = await _client.TranscribeParakeetAsync(
            request.SourceAudioPath,
            request.LanguageHint,
            cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException($"Parakeet transcription failed: {result.ErrorMessage}");

        var artifactDir = Path.GetDirectoryName(request.OutputJsonPath);
        if (!string.IsNullOrEmpty(artifactDir))
            Directory.CreateDirectory(artifactDir);

        var transcript = new TranscriptArtifact
        {
            Language = result.Language,
            LanguageProbability = result.LanguageProbability,
            Segments =
            [
                .. result.Segments.Select(s => new TranscriptSegmentArtifact
                {
                    Start = s.StartSeconds,
                    End = s.EndSeconds,
                    Text = s.Text,
                })
            ],
        };

        await File.WriteAllTextAsync(
            request.OutputJsonPath,
            ArtifactJson.SerializeTranscript(transcript),
            cancellationToken);

        _log.Info($"[ParakeetTranscription] Complete: {result.Segments.Count} segments, lang={result.Language}");
        return result;
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
        ContainerizedProviderReadiness.CheckTranscription(settings, keyStore);
}
