using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public sealed class OpenAiWhisperTranscriptionProvider : ITranscriptionProvider
{
    private readonly AppLog _log;
    private readonly string _apiKey;
    private readonly Func<OpenAiApiClient> _clientFactory;

    public OpenAiWhisperTranscriptionProvider(
        AppLog log,
        string apiKey,
        Func<OpenAiApiClient>? clientFactory = null)
    {
        _log = log;
        _apiKey = apiKey;
        _clientFactory = clientFactory ?? (() => new OpenAiApiClient(_apiKey));
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new ProviderReadiness(false, "API key missing for provider 'OpenAI Whisper API'.");

        return ProviderReadiness.Ready;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        _log.Info($"[OpenAIWhisper] Transcribing: {request.SourceAudioPath} (model={request.ModelName})");

        using var client = _clientFactory();
        var payload = await client.TranscribeAudioAsync(
            request.SourceAudioPath,
            request.ModelName,
            request.LanguageHint,
            cancellationToken);

        var segments = new List<TranscriptSegment>();
        if (payload.Segments.Count > 0)
        {
            foreach (var segment in payload.Segments)
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                    segments.Add(new TranscriptSegment(segment.StartSeconds, segment.EndSeconds, segment.Text));
            }
        }
        else if (!string.IsNullOrWhiteSpace(payload.Text))
        {
            segments.Add(new TranscriptSegment(0, 0, payload.Text));
        }

        var transcriptArtifact = new TranscriptArtifact
        {
            Language = string.IsNullOrWhiteSpace(payload.Language)
                ? (request.LanguageHint ?? "unknown")
                : payload.Language,
            LanguageProbability = 1.0,
            Segments =
            [
                .. System.Linq.Enumerable.Select(segments, s => new TranscriptSegmentArtifact
                {
                    Start = s.StartSeconds,
                    End = s.EndSeconds,
                    Text = s.Text,
                })
            ]
        };

        var outputDir = Path.GetDirectoryName(request.OutputJsonPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllTextAsync(request.OutputJsonPath, ArtifactJson.SerializeTranscript(transcriptArtifact), cancellationToken);

        _log.Info($"[OpenAIWhisper] Complete: {segments.Count} segments.");

        return new TranscriptionResult(
            true,
            segments,
            transcriptArtifact.Language ?? "unknown",
            transcriptArtifact.LanguageProbability,
            null);
    }
}
