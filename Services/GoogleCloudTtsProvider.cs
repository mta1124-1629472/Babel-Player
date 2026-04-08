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
public sealed class GoogleCloudTtsProvider : ITtsProvider, IDisposable
{
    private readonly AppLog _log;
    private readonly string _apiKey;
    private readonly Lazy<GoogleApiClient> _clientLazy;

    /// <summary>
    /// Initializes a new instance of <see cref="GoogleCloudTtsProvider"/> with the specified logger, API key, and an optional factory for the Google API client.
    /// </summary>
    /// <param name="log">Application logger used for informational messages about TTS operations.</param>
    /// <param name="apiKey">API key used to construct the Google API client.</param>
    /// <param name="clientFactory">Optional factory that produces a <see cref="GoogleApiClient"/>; if null, a default factory that calls <c>new GoogleApiClient(apiKey)</c> is used. The client is created lazily and cached using thread-safe initialization.</param>
    public GoogleCloudTtsProvider(
        AppLog log,
        string apiKey,
        Func<GoogleApiClient>? clientFactory = null)
    {
        _log = log;
        _apiKey = apiKey;
        _clientLazy = new Lazy<GoogleApiClient>(clientFactory ?? (() => new GoogleApiClient(_apiKey)), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Verifies that the provider has a configured API key and reports readiness.
    /// </summary>
    /// <param name="settings">Unused by this provider.</param>
    /// <param name="keyStore">Unused by this provider.</param>
    /// <returns>
    /// A <see cref="ProviderReadiness"/> that is ready when an API key is present; otherwise not ready with an explanatory message.
    /// </returns>
    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new ProviderReadiness(false, "API key missing for provider 'Google Cloud TTS'.");

        return ProviderReadiness.Ready;
    }

    /// <summary>
    /// Synthesizes speech for a translation artifact by concatenating translated segments, generating audio via Google Cloud TTS, and writing the resulting audio file to disk.
    /// </summary>
    /// <param name="request">Parameters including TranslationJsonPath, OutputAudioPath, and VoiceName used for synthesis and output.</param>
    /// <param name="cancellationToken">Token to cancel asynchronous operations.</param>
    /// <returns>A TtsResult describing the generated audio file path, requested voice, byte length, and success status.</returns>
    /// <exception cref="FileNotFoundException">Thrown if <see cref="TtsRequest.TranslationJsonPath"/> does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the translation artifact contains no translated text to synthesize.</exception>
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
        var client = _clientLazy.Value;
        var audioBytes = await client.SynthesizeSpeechAsync(combinedText, voiceName, cancellationToken);

        var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllBytesAsync(request.OutputAudioPath, audioBytes, cancellationToken);

        _log.Info($"[GoogleCloudTTS] Generated combined audio: {request.OutputAudioPath} ({audioBytes.Length} bytes)");

        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, audioBytes.Length, null);
    }

    /// <summary>
    /// Synthesizes speech for a single text segment and writes the resulting audio file to disk.
    /// </summary>
    /// <param name="request">Request containing the segment text, target voice name, and output audio path.</param>
    /// <param name="cancellationToken">Token used to cancel the synthesis and file-write operations.</param>
    /// <returns>A TtsResult indicating whether synthesis succeeded, the output audio path, the requested voice name, the length of the generated audio in bytes, and null error on success.</returns>
    /// <exception cref="ArgumentException">Thrown when <see cref="SingleSegmentTtsRequest.Text"/> is null, empty, or whitespace.</exception>
    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Segment text cannot be empty", nameof(request));

        var voiceName = ResolveVoiceName(request.VoiceName);
        var client = _clientLazy.Value;
        var audioBytes = await client.SynthesizeSpeechAsync(request.Text, voiceName, cancellationToken);

        var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllBytesAsync(request.OutputAudioPath, audioBytes, cancellationToken);

        _log.Info($"[GoogleCloudTTS] Generated segment audio: {request.OutputAudioPath} ({audioBytes.Length} bytes)");

        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, audioBytes.Length, null);
    }

    /// <summary>
    /// Maps a user-facing model or voice key to the corresponding Google Cloud TTS voice identifier.
    /// </summary>
    /// <param name="modelOrVoice">A model or voice key such as "standard", "wavenet", or "neural2".</param>
    /// <returns>The Google Cloud TTS voice identifier for the given key (e.g. "en-US-Standard-C", "en-US-Wavenet-D", "en-US-Neural2-D"); defaults to "en-US-Standard-C".</returns>
    private static string ResolveVoiceName(string modelOrVoice) => modelOrVoice switch
    {
        "standard" => "en-US-Standard-C",
        "wavenet" => "en-US-Wavenet-D",
        "neural2" => "en-US-Neural2-D",
        _ => "en-US-Standard-C"
    };

    /// <summary>
    /// Disposes the cached GoogleApiClient instance if it has been created.
    /// </summary>
    public void Dispose()
    {
        if (_clientLazy.IsValueCreated)
            _clientLazy.Value.Dispose();
    }
}
