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
public sealed class OpenAiTtsProvider : ITtsProvider, IDisposable
{
    private readonly AppLog _log;
    private readonly string _apiKey;
    private readonly Lazy<OpenAiApiClient> _clientLazy;

    /// <summary>
    /// Initializes a new OpenAiTtsProvider that generates speech via OpenAI and defers creation of the API client until it is first needed.
    /// </summary>
    /// <param name="log">Application logger used to record provider activity.</param>
    /// <param name="apiKey">OpenAI API key used by the default client if no factory is provided.</param>
    /// <param name="clientFactory">Optional factory to create an <see cref="OpenAiApiClient"/>; when not provided, a default client that uses <paramref name="apiKey"/> is created lazily and reused.</param>
    public OpenAiTtsProvider(
        AppLog log,
        string apiKey,
        Func<OpenAiApiClient>? clientFactory = null)
    {
        _log = log;
        _apiKey = apiKey;
        _clientLazy = new Lazy<OpenAiApiClient>(clientFactory ?? (() => new OpenAiApiClient(_apiKey)), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Determines whether the OpenAI TTS provider is ready by verifying that an API key is configured.
    /// </summary>
    /// <returns>
    /// A <see cref="ProviderReadiness"/> that is ready when an API key is present; otherwise a readiness with `IsReady` false and a message indicating the API key is missing.
    /// <summary>
    /// Determines whether the provider has a configured API key and is ready to make requests.
    /// </summary>
    /// <returns>A <see cref="ProviderReadiness"/> indicating readiness; if the stored API key is null, empty, or whitespace, returns a not-ready instance with message "API key missing for provider 'OpenAI API'."; otherwise returns <see cref="ProviderReadiness.Ready"/>.</returns>
    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new ProviderReadiness(false, "API key missing for provider 'OpenAI API'.");

        return ProviderReadiness.Ready;
    }

    /// <summary>
    /// Generates a single audio file by concatenating translated segments from a translation artifact and synthesizing speech using the OpenAI TTS model.
    /// </summary>
    /// <param name="request">Parameters for generation, including the translation JSON path, output audio path, and voice name.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A <see cref="TtsResult"/> representing the generated audio file and its metadata (output path, voice, byte length).</returns>
    /// <exception cref="FileNotFoundException">Thrown when the translation JSON file specified by <paramref name="request"/> does not exist.</exception>
    /// <summary>
    /// Synthesizes speech for an entire translation artifact and writes the resulting audio file to the requested path.
    /// </summary>
    /// <param name="request">Request describing the translation artifact location, target output audio path, and voice to use.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A TtsResult with success status, the output audio path, selected voice name, and the generated audio byte length.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the translation JSON file at <paramref name="request"/>.TranslationJsonPath does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the translation artifact contains no translated text to synthesize.</exception>
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
        var client = _clientLazy.Value;
        var audioBytes = await client.CreateSpeechAsync(combinedText, model, "alloy", cancellationToken);

        var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllBytesAsync(request.OutputAudioPath, audioBytes, cancellationToken);

        _log.Info($"[OpenAITTS] Generated combined audio: {request.OutputAudioPath} ({audioBytes.Length} bytes)");

        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, audioBytes.Length, null);
    }

    /// <summary>
    /// Generates speech audio for a single text segment and writes the resulting audio file to the specified output path.
    /// </summary>
    /// <param name="request">A request containing the segment Text to synthesize, the desired VoiceName (model), and the OutputAudioPath where the audio will be written.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>A <see cref="TtsResult"/> with the success status, output path, voice name, and audio byte length when generation succeeds.</returns>
    /// <summary>
    /// Synthesizes speech for a single segment and writes the resulting audio file to disk.
    /// </summary>
    /// <param name="request">Request containing the segment text, target voice name, and output audio path.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="TtsResult"/> with success set to true, the output file path, the voice name used, the number of bytes written, and a null error message.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <see cref="SingleSegmentTtsRequest.Text"/> is null, empty, or whitespace.</exception>
    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Segment text cannot be empty", nameof(request));

        var model = NormalizeModel(request.VoiceName);
        var client = _clientLazy.Value;
        var audioBytes = await client.CreateSpeechAsync(request.Text, model, "alloy", cancellationToken);

        var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllBytesAsync(request.OutputAudioPath, audioBytes, cancellationToken);

        _log.Info($"[OpenAITTS] Generated segment audio: {request.OutputAudioPath} ({audioBytes.Length} bytes)");

        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, audioBytes.Length, null);
    }

    /// <summary>
    /// Map a requested model identifier to a supported OpenAI TTS model name.
    /// </summary>
    /// <param name="selected">Requested model identifier (e.g., "tts-1", "tts-1-hd", "gpt-4o-mini-tts").</param>
    /// <returns>Canonical model name to use for OpenAI TTS; returns "tts-1" for unknown inputs.</returns>
    private static string NormalizeModel(string selected) => selected switch
    {
        "tts-1" => "tts-1",
        "tts-1-hd" => "tts-1-hd",
        "gpt-4o-mini-tts" => "gpt-4o-mini-tts",
        _ => "tts-1"
    };

    /// <summary>
    /// Disposes unmanaged resources held by the provider by disposing the lazily-created OpenAiApiClient if it has been initialized.
    /// </summary>
    /// <remarks>
    /// If the underlying OpenAiApiClient has not yet been created, this method returns without action.
    /// </remarks>
    public void Dispose()
    {
        if (_clientLazy.IsValueCreated)
            _clientLazy.Value.Dispose();
    }
}
