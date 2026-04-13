using System;
using System.Collections.Generic;
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
public sealed class ElevenLabsTtsProvider : ITtsProvider, IDisposable
{
    public int MaxConcurrency => 10;

    /// <summary>
    /// ElevenLabs pre-made "Rachel" voice — available on all subscription tiers.
    /// Used as the default character voice for all synthesis requests.
    /// </summary>
    public const string DefaultVoiceId = "21m00Tcm4TlvDq8ikWAM";

    private readonly AppLog _log;
    private readonly string _apiKey;
    private readonly Lazy<ElevenLabsApiClient> _clientLazy;
    private readonly IAudioProcessingService? _audioProcessingService;

    /// <summary>
    /// Creates a new ElevenLabsTtsProvider and configures a lazily initialized ElevenLabsApiClient.
    /// </summary>
    /// <param name="log">Application logger used for provider diagnostics.</param>
    /// <param name="apiKey">ElevenLabs API key used to authenticate requests.</param>
    /// <summary>
    /// Initializes a new instance of <see cref="ElevenLabsTtsProvider"/>.
    /// </summary>
    /// <param name="log">Application logger used for informational and error messages.</param>
    /// <param name="apiKey">ElevenLabs API key used to create the API client when a default factory is used.</param>
    /// <param name="clientFactory">Optional factory to create an <see cref="ElevenLabsApiClient"/>; if null, a default factory that uses <paramref name="apiKey"/> will be used and the client instance will be created on first use.</param>
    /// <param name="audioProcessingService">Optional audio processing service for combining segments; if null, falls back to AudioConcatUtility.</param>
    public ElevenLabsTtsProvider(
        AppLog log,
        string apiKey,
        Func<ElevenLabsApiClient>? clientFactory = null,
        IAudioProcessingService? audioProcessingService = null)
    {
        _log = log;
        _apiKey = apiKey;
        _clientLazy = new Lazy<ElevenLabsApiClient>(clientFactory ?? (() => new ElevenLabsApiClient(_apiKey)), LazyThreadSafetyMode.ExecutionAndPublication);
        _audioProcessingService = audioProcessingService;
    }

    /// <summary>
    /// Determines whether the ElevenLabs TTS provider is configured and ready to use.
    /// </summary>
    /// <returns>
    /// `ProviderReadiness.Ready` if an ElevenLabs API key is configured; otherwise a `ProviderReadiness` with `Success = false` and an explanatory message.
    /// <summary>
    /// Determines whether the ElevenLabs TTS provider is configured to operate.
    /// </summary>
    /// <param name="settings">Application settings (not used by this provider).</param>
    /// <param name="keyStore">Optional API key store (not used by this provider).</param>
    /// <returns>
    /// A <see cref="ProviderReadiness"/> that is ready when an ElevenLabs API key is present; otherwise indicates not ready with the message "ElevenLabs API key is not set.".
    /// </returns>
    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new ProviderReadiness(false, "ElevenLabs API key is not set.");

        return ProviderReadiness.Ready;
    }

    /// <summary>
    /// Generates combined TTS audio from all segments in a translation JSON file.
    /// Reads the translation, generates TTS for each segment, then stitches them together.
    /// </summary>
    /// <param name="request">TTS request containing translation JSON path, output audio path, and voice name.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A TtsResult describing the combined audio output.</returns>
    public async Task<TtsResult> GenerateTtsAsync(
        TtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TranslationJsonPath))
            throw new ArgumentException("Translation JSON path cannot be null or empty.", nameof(request));
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");
        if (string.IsNullOrWhiteSpace(request.OutputAudioPath))
            throw new ArgumentException("Output audio path cannot be null or empty.", nameof(request));

        _log.Info($"[ElevenLabsTTS] Starting combined TTS generation from {request.TranslationJsonPath}");

        var translationData = await ArtifactJson.LoadTranslationAsync(request.TranslationJsonPath, cancellationToken);
        var candidateSegments = translationData.Segments?
            .Where(seg => !string.IsNullOrWhiteSpace(seg.Id) && !string.IsNullOrWhiteSpace(seg.TranslatedText))
            .ToList()
            ?? [];

        if (candidateSegments.Count == 0)
            throw new InvalidOperationException($"No valid segments with translated text found in {request.TranslationJsonPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"babel-elevenlabs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var segmentPaths = new List<string>();

            for (int i = 0; i < candidateSegments.Count; i++)
            {
                var seg = candidateSegments[i];
                var segmentPath = Path.Combine(tempDir, $"{seg.Id}.mp3");

                var segResult = await GenerateSegmentTtsAsync(
                    new SingleSegmentTtsRequest(seg.TranslatedText!, segmentPath, request.VoiceName),
                    cancellationToken);

                if (segResult.Success && File.Exists(segmentPath))
                {
                    segmentPaths.Add(segmentPath);
                    _log.Info($"[ElevenLabsTTS] Generated segment {i + 1}/{candidateSegments.Count}: {seg.Id}");
                }
                else
                {
                    throw new InvalidOperationException($"Failed to generate TTS for segment {seg.Id}");
                }
            }

            if (_audioProcessingService is not null)
            {
                await _audioProcessingService.CombineAudioSegmentsAsync(segmentPaths, request.OutputAudioPath, cancellationToken);
            }
            else
            {
                await AudioConcatUtility.CombineAudioSegmentsAsync(segmentPaths, request.OutputAudioPath, _log, cancellationToken);
            }

            if (!File.Exists(request.OutputAudioPath))
                throw new InvalidOperationException($"Combined audio file was not created at {request.OutputAudioPath}");

            var fileSize = new FileInfo(request.OutputAudioPath).Length;
            _log.Info($"[ElevenLabsTTS] Combined TTS generation complete: {request.OutputAudioPath} ({fileSize} bytes)");

            return new TtsResult(true, request.OutputAudioPath, request.VoiceName, fileSize, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    /// <summary>
    /// Generates speech for a single translated text segment.
    /// <paramref name="request.VoiceName"/> maps to the ElevenLabs model ID;
    /// <see cref="DefaultVoiceId"/> is used for character voice.
    /// <summary>
    /// Generates speech audio for a single translated segment and writes the resulting audio file to the request's output path.
    /// </summary>
    /// <param name="request">Single-segment TTS request. `request.Text` must be non-empty; `request.OutputAudioPath` is the file path to write; `request.VoiceName` selects the synthesis model.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>A TtsResult describing the operation: success flag, output path, voice name, byte length of the written audio, and any error (null on success).</returns>
    /// <summary>
    /// Generates speech audio for a single translated segment and writes the resulting audio file to the specified output path.
    /// </summary>
    /// <param name="request">The segment request containing the text to synthesize, the desired voice name, and the output audio path. <c>Text</c> must not be empty or whitespace.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A <see cref="TtsResult"/> with Success set to <c>true</c>, the output path, the voice name used, and AudioLength equal to the number of bytes written.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="request"/> has an empty or whitespace <c>Text</c> value.</exception>
    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Segment text cannot be empty.", nameof(request));

        var modelId = NormalizeModelId(request.VoiceName);

        _log.Info($"[ElevenLabsTTS] Generating segment audio: {request.Text[..Math.Min(30, request.Text.Length)]}... model={modelId}");

        var client = _clientLazy.Value;

        var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        try
        {
            await client.DownloadSpeechAsync(request.Text, DefaultVoiceId, request.OutputAudioPath, modelId, cancellationToken)
                .ConfigureAwait(false);
            var fileLength = new FileInfo(request.OutputAudioPath).Length;

            _log.Info($"[ElevenLabsTTS] Segment audio written: {request.OutputAudioPath} ({fileLength} bytes)");

            return new TtsResult(true, request.OutputAudioPath, request.VoiceName, fileLength, null);
        }
        catch (Exception)
        {
            if (File.Exists(request.OutputAudioPath))
            {
                try
                {
                    File.Delete(request.OutputAudioPath);
                    _log.Info($"[ElevenLabsTTS] Deleted partial file after failure: {request.OutputAudioPath}");
                }
                catch (Exception cleanupEx)
                {
                    _log.Error($"[ElevenLabsTTS] Failed to delete partial file {request.OutputAudioPath}: {cleanupEx.Message}", cleanupEx);
                }
            }
            throw;
        }
    }

    // Map the VoiceName field (which holds the selected model/quality tier in Babel Player's
    // ElevenLabs configuration) to a valid ElevenLabs model ID. Falls back to the
    /// <summary>
        /// Map a provided voice name to the corresponding ElevenLabs model identifier.
        /// </summary>
        /// <param name="voiceName">The requested voice name or model hint.</param>
        /// <returns>The normalized model id: "eleven_multilingual_v2", "eleven_turbo_v2_5", or "eleven_flash_v2_5"; defaults to "eleven_multilingual_v2" for unrecognized or empty values.</returns>
    private static string NormalizeModelId(string voiceName) =>
        voiceName switch
        {
            "eleven_multilingual_v2"  => "eleven_multilingual_v2",
            "eleven_turbo_v2_5"       => "eleven_turbo_v2_5",
            "eleven_flash_v2_5"       => "eleven_flash_v2_5",
            _                         => "eleven_multilingual_v2",
        };

    /// <summary>
    /// Disposes the underlying ElevenLabs API client if it has been created.
    /// </summary>
    /// <remarks>
    /// If the lazy client has not been instantiated, this method performs no action.
    /// </remarks>
    public void Dispose()
    {
        if (_clientLazy.IsValueCreated)
            _clientLazy.Value.Dispose();
    }
}