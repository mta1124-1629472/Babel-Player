using System;
using System.Collections.Generic;
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
/// OpenAI-backed cloud TTS provider.
///
/// In Babel Player's current provider-model UX, <see cref="TtsRequest.VoiceName"/> is used
/// for the selected provider model (e.g. tts-1, tts-1-hd, gpt-4o-mini-tts). OpenAI's
/// character voice is fixed to a default for now ("alloy") to avoid introducing a new
/// settings dimension in this milestone.
/// </summary>
public sealed class OpenAiTtsProvider : ITtsProvider
{
    private readonly AppLog _log;
    private readonly string _apiKey;
    private readonly Func<OpenAiApiClient> _clientFactory;

    public OpenAiTtsProvider(
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
            return new ProviderReadiness(false, "API key missing for provider 'OpenAI API'.");

        return ProviderReadiness.Ready;
    }

    public async Task<TtsResult> GenerateTtsAsync(
        TtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");

        var translation = await ArtifactJson.LoadTranslationAsync(request.TranslationJsonPath, cancellationToken);
        var combinedText = string.Join(" ",
            (translation.Segments ?? [])
                .Select(s => s.TranslatedText)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!.Trim()));

        if (string.IsNullOrWhiteSpace(combinedText))
            throw new InvalidOperationException("No translated text found in translation artifact.");

        var model = NormalizeModel(request.VoiceName);
        using var client = _clientFactory();
        var audioBytes = await client.CreateSpeechAsync(combinedText, model, "alloy", cancellationToken);

        var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllBytesAsync(request.OutputAudioPath, audioBytes, cancellationToken);

        _log.Info($"[OpenAITTS] Generated combined audio: {request.OutputAudioPath} ({audioBytes.Length} bytes)");

        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, audioBytes.Length, null);
    }

    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Segment text cannot be empty", nameof(request));

        var model = NormalizeModel(request.VoiceName);
        using var client = _clientFactory();
        var audioBytes = await client.CreateSpeechAsync(request.Text, model, "alloy", cancellationToken);

        var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllBytesAsync(request.OutputAudioPath, audioBytes, cancellationToken);

        _log.Info($"[OpenAITTS] Generated segment audio: {request.OutputAudioPath} ({audioBytes.Length} bytes)");

        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, audioBytes.Length, null);
    }

    private static string NormalizeModel(string selected) => selected switch
    {
        "tts-1" => "tts-1",
        "tts-1-hd" => "tts-1-hd",
        "gpt-4o-mini-tts" => "gpt-4o-mini-tts",
        _ => "tts-1"
    };
}
