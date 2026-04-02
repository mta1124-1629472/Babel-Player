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
/// Google Cloud TTS provider wired for cloud runtime only.
///
/// The UI model selection (standard/wavenet/neural2) maps to a default voice name.
/// This keeps parity with existing provider-model UX where <see cref="TtsRequest.VoiceName"/>
/// carries the selected model option.
/// </summary>
public sealed class GoogleCloudTtsProvider : ITtsProvider
{
    private readonly AppLog _log;
    private readonly string _apiKey;
    private readonly Func<GoogleApiClient> _clientFactory;

    public GoogleCloudTtsProvider(
        AppLog log,
        string apiKey,
        Func<GoogleApiClient>? clientFactory = null)
    {
        _log = log;
        _apiKey = apiKey;
        _clientFactory = clientFactory ?? (() => new GoogleApiClient(_apiKey));
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new ProviderReadiness(false, "API key missing for provider 'Google Cloud TTS'.");

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

        var voiceName = ResolveVoiceName(request.VoiceName);
        using var client = _clientFactory();
        var audioBytes = await client.SynthesizeSpeechAsync(combinedText, voiceName, cancellationToken);

        var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllBytesAsync(request.OutputAudioPath, audioBytes, cancellationToken);

        _log.Info($"[GoogleCloudTTS] Generated combined audio: {request.OutputAudioPath} ({audioBytes.Length} bytes)");

        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, audioBytes.Length, null);
    }

    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Segment text cannot be empty", nameof(request));

        var voiceName = ResolveVoiceName(request.VoiceName);
        using var client = _clientFactory();
        var audioBytes = await client.SynthesizeSpeechAsync(request.Text, voiceName, cancellationToken);

        var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllBytesAsync(request.OutputAudioPath, audioBytes, cancellationToken);

        _log.Info($"[GoogleCloudTTS] Generated segment audio: {request.OutputAudioPath} ({audioBytes.Length} bytes)");

        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, audioBytes.Length, null);
    }

    private static string ResolveVoiceName(string modelOrVoice) => modelOrVoice switch
    {
        "standard" => "en-US-Standard-C",
        "wavenet" => "en-US-Wavenet-D",
        "neural2" => "en-US-Neural2-D",
        _ => "en-US-Standard-C"
    };
}
