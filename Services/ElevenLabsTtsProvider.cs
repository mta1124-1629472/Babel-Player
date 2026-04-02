using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// TTS provider backed by the ElevenLabs REST API.
///
/// Voice selection in Babel Player maps the "voice" field (AppSettings.TtsVoice)
/// to the ElevenLabs <em>model</em> ID (quality tier, e.g.
/// <c>eleven_multilingual_v2</c>). The character voice used for synthesis is
/// <see cref="DefaultVoiceId"/> (Rachel) — a pre-made ElevenLabs voice available
/// on all subscription tiers. Future work can expose per-user voice ID selection.
/// </summary>
public sealed class ElevenLabsTtsProvider : ITtsProvider
{
    /// <summary>
    /// ElevenLabs pre-made "Rachel" voice — available on all subscription tiers.
    /// Used as the default character voice for all synthesis requests.
    /// </summary>
    public const string DefaultVoiceId = "21m00Tcm4TlvDq8ikWAM";

    private readonly AppLog _log;
    private readonly string _apiKey;
    private readonly Func<ElevenLabsApiClient> _clientFactory;

    public ElevenLabsTtsProvider(
        AppLog log,
        string apiKey,
        Func<ElevenLabsApiClient>? clientFactory = null)
    {
        _log = log;
        _apiKey = apiKey;
        _clientFactory = clientFactory ?? (() => new ElevenLabsApiClient(_apiKey));
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new ProviderReadiness(false, "ElevenLabs API key is not set.");

        return ProviderReadiness.Ready;
    }

    /// <summary>
    /// Generates speech for all translated segments combined into one audio file.
    /// <paramref name="request.VoiceName"/> maps to the ElevenLabs model ID
    /// (quality tier); <see cref="DefaultVoiceId"/> is used for character voice.
    /// </summary>
    public async Task<TtsResult> GenerateTtsAsync(
        TtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");

        var translationArtifact = await ArtifactJson.LoadTranslationAsync(request.TranslationJsonPath, cancellationToken);
        var texts = (translationArtifact.Segments ?? [])
            .Select(s => s.TranslatedText ?? string.Empty)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (texts.Count == 0)
            throw new InvalidOperationException("No translated text found in translation artifact.");

        var combinedText = string.Join(" ", texts);
        var modelId = NormalizeModelId(request.VoiceName);

        _log.Info($"[ElevenLabsTTS] Generating combined audio: {texts.Count} segments, model={modelId}");

        using var client = _clientFactory();
        var audioBytes = await client.TextToSpeechAsync(combinedText, DefaultVoiceId, modelId, cancellationToken);

        var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllBytesAsync(request.OutputAudioPath, audioBytes, cancellationToken);

        _log.Info($"[ElevenLabsTTS] Combined audio written: {request.OutputAudioPath} ({audioBytes.Length} bytes)");

        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, audioBytes.Length, null);
    }

    /// <summary>
    /// Generates speech for a single translated text segment.
    /// <paramref name="request.VoiceName"/> maps to the ElevenLabs model ID;
    /// <see cref="DefaultVoiceId"/> is used for character voice.
    /// </summary>
    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Segment text cannot be empty.", nameof(request));

        var modelId = NormalizeModelId(request.VoiceName);

        _log.Info($"[ElevenLabsTTS] Generating segment audio: {request.Text[..Math.Min(30, request.Text.Length)]}... model={modelId}");

        using var client = _clientFactory();
        var audioBytes = await client.TextToSpeechAsync(request.Text, DefaultVoiceId, modelId, cancellationToken);

        var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllBytesAsync(request.OutputAudioPath, audioBytes, cancellationToken);

        _log.Info($"[ElevenLabsTTS] Segment audio written: {request.OutputAudioPath} ({audioBytes.Length} bytes)");

        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, audioBytes.Length, null);
    }

    // Map the VoiceName field (which holds the selected model/quality tier in Babel Player's
    // ElevenLabs configuration) to a valid ElevenLabs model ID. Falls back to the
    // highest-quality multilingual model if the value is unrecognised or empty.
    private static string NormalizeModelId(string voiceName) =>
        voiceName switch
        {
            "eleven_multilingual_v2"  => "eleven_multilingual_v2",
            "eleven_turbo_v2_5"       => "eleven_turbo_v2_5",
            "eleven_flash_v2_5"       => "eleven_flash_v2_5",
            _                         => "eleven_multilingual_v2",
        };
}
