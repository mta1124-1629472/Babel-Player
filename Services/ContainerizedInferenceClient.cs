using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed class ContainerizedInferenceClient
{
    private const string CapabilitiesWarmupPrefix = "Capabilities probe is still warming or failed";
    private readonly HttpClient _httpClient;
    private readonly AppLog _log;
    private readonly string _inferenceServiceUrl;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };

    public ContainerizedInferenceClient(string inferenceServiceUrl, AppLog log)
        : this(inferenceServiceUrl, log, null)
    {
    }

    public ContainerizedInferenceClient(string inferenceServiceUrl, AppLog log, HttpClient? httpClient)
    {
        _inferenceServiceUrl = NormalizeBaseUrl(inferenceServiceUrl);
        _log = log;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public Task<ContainerHealthStatus> CheckHealthAsync(
        CancellationToken cancellationToken = default) =>
        ProbeHealthAsync(_httpClient, _inferenceServiceUrl, cancellationToken);

    public static async Task<ContainerHealthStatus> CheckHealthAsync(
        string serviceUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = timeout };
            return await ProbeHealthAsync(http, NormalizeBaseUrl(serviceUrl), cancellationToken);
        }
        catch (Exception ex)
        {
            var normalizedUrl = NormalizeBaseUrl(serviceUrl);
            return ContainerHealthStatus.Unavailable(normalizedUrl, ex.Message);
        }
    }

    /// <summary>
    /// Blocking health check intended for background-thread startup probes.
    /// Uses both /health/live and /capabilities so provider readiness is stage-aware.
    /// </summary>
    public static ContainerHealthStatus CheckHealth(string serviceUrl, int timeoutSeconds = 5)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            return ProbeHealthAsync(http, NormalizeBaseUrl(serviceUrl), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            var normalizedUrl = NormalizeBaseUrl(serviceUrl);
            return ContainerHealthStatus.Unavailable(normalizedUrl, ex.Message);
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        string audioFilePath,
        string modelName = "base",
        string? language = null,
        string cpuComputeType = "int8",
        int cpuThreads = 0,
        int numWorkers = 1,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(audioFilePath))
                throw new FileNotFoundException($"Audio file not found: {audioFilePath}");

            _log.Info($"Transcribing with containerized service: {audioFilePath}");

            using var content = new MultipartFormDataContent();
            using var fileStream = File.OpenRead(audioFilePath);
            content.Add(new StreamContent(fileStream), "file", Path.GetFileName(audioFilePath));
            content.Add(new StringContent(modelName), "model");
            if (language != null)
                content.Add(new StringContent(language), "language");
            content.Add(new StringContent(string.IsNullOrWhiteSpace(cpuComputeType) ? "int8" : cpuComputeType), "cpu_compute_type");
            if (cpuThreads > 0)
                content.Add(new StringContent(cpuThreads.ToString()), "cpu_threads");
            content.Add(new StringContent((numWorkers < 1 ? 1 : numWorkers).ToString()), "num_workers");

            using var response = await _httpClient.PostAsync(
                $"{_inferenceServiceUrl}/transcribe",
                content,
                cancellationToken);

            var result = await DeserializeResponseAsync<TranscriptionApiResponseDto>(response, cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException($"Transcription error: {result.ErrorMessage}");

            var segments = new List<TranscriptSegment>();
            foreach (var seg in result.Segments ?? [])
            {
                if (!string.IsNullOrWhiteSpace(seg.Text))
                    segments.Add(new TranscriptSegment(seg.Start, seg.End, seg.Text));
            }

            _log.Info($"Transcription complete: {segments.Count} segments");

            return new TranscriptionResult(
                true,
                segments,
                result.Language ?? "unknown",
                result.LanguageProbability,
                null);
        }
        catch (Exception ex)
        {
            _log.Error($"Transcription failed: {ex.Message}", ex);
            return new TranscriptionResult(false, [], "unknown", 0.0, ex.Message);
        }
    }

    /// <summary>
    /// Translates segments from a transcript JSON string.
    /// <paramref name="transcriptJson"/> must be the raw transcript artifact JSON.
    /// </summary>
    public async Task<TranslationResult> TranslateAsync(
        string transcriptJson,
        string sourceLanguage,
        string targetLanguage,
        string model,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _log.Info($"Translating {sourceLanguage} -> {targetLanguage}");

            using var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("transcript_json", transcriptJson),
                new KeyValuePair<string, string>("source_language", sourceLanguage),
                new KeyValuePair<string, string>("target_language", targetLanguage),
                new KeyValuePair<string, string>("model", model),
            ]);

            using var response = await _httpClient.PostAsync(
                $"{_inferenceServiceUrl}/translate",
                content,
                cancellationToken);

            var result = await DeserializeResponseAsync<TranslationApiResponseDto>(response, cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException($"Translation error: {result.ErrorMessage}");

            var translatedSegments = new List<TranslatedSegment>();
            foreach (var seg in result.Segments ?? [])
            {
                translatedSegments.Add(new TranslatedSegment(
                    seg.Start,
                    seg.End,
                    seg.Text ?? string.Empty,
                    seg.TranslatedText ?? string.Empty,
                    SpeakerId: string.IsNullOrWhiteSpace(seg.SpeakerId) ? null : seg.SpeakerId));
            }

            _log.Info($"Translation complete: {translatedSegments.Count} segments");

            return new TranslationResult(
                true,
                translatedSegments,
                result.SourceLanguage ?? sourceLanguage,
                result.TargetLanguage ?? targetLanguage,
                null);
        }
        catch (Exception ex)
        {
            _log.Error($"Translation failed: {ex.Message}", ex);
            return new TranslationResult(false, [], sourceLanguage, targetLanguage, ex.Message);
        }
    }

    /// <summary>
    /// Generate speech audio for the provided text using the specified voice.
    /// </summary>
    /// <param name="text">The text to synthesize to speech.</param>
    /// <param name="voice">The voice identifier to use for synthesis.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the HTTP request.</param>
    /// <returns>
    /// A <see cref="TtsResult"/> containing success state, the generated audio path (empty on failure), the voice used, the file size in bytes, and an error message when unsuccessful.
    /// </returns>
    public async Task<TtsResult> TextToSpeechAsync(
        string text,
        string voice = "en-US-AriaNeural",
        CancellationToken cancellationToken = default)
    {
        try
        {
            _log.Info($"Generating TTS with voice: {voice}");

            using var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("text", text),
                new KeyValuePair<string, string>("voice", voice)
            ]);

            using var response = await _httpClient.PostAsync(
                $"{_inferenceServiceUrl}/tts",
                content,
                cancellationToken);

            var result = await DeserializeResponseAsync<TtsApiResponseDto>(response, cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException($"TTS error: {result.ErrorMessage}");

            _log.Info($"TTS generation complete: {result.FileSizeBytes} bytes");

            return new TtsResult(true, result.AudioPath ?? "", result.Voice ?? "", result.FileSizeBytes, null);
        }
        catch (Exception ex)
        {
            _log.Error($"TTS failed: {ex.Message}", ex);
            return new TtsResult(false, "", "", 0, ex.Message);
        }
    }

    /// <summary>
    /// Performs speaker diarization on the given audio file using the containerized inference service.
    /// </summary>
    /// <param name="audioFilePath">Path to the audio file to diarize.</param>
    /// <param name="engine">Requested diarization engine identifier (for example, <see cref="ProviderNames.WeSpeakerLocal"/> or its legacy alias).</param>
    /// <param name="minSpeakers">Optional hint for the minimum number of speakers to detect.</param>
    /// <param name="maxSpeakers">Optional hint for the maximum number of speakers to detect.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="DiarizationResult"/> containing the success flag, normalized diarization segments, the determined speaker count, and an error message if the operation failed.
    /// </returns>
    public async Task<DiarizationResult> DiarizeAsync(
        string audioFilePath,
        string engine,
        int? minSpeakers = null,
        int? maxSpeakers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(audioFilePath))
                throw new FileNotFoundException($"Audio file not found: {audioFilePath}");

            var normalizedEngine = NormalizeDiarizationEngine(engine);
            var endpoint = normalizedEngine == ProviderNames.WeSpeakerLocal ? "/diarize/wespeaker" : "/diarize";
            _log.Info($"Diarizing with containerized service: {audioFilePath} (engine={normalizedEngine})");

            using var content = new MultipartFormDataContent();
            using var fileStream = File.OpenRead(audioFilePath);
            content.Add(new StreamContent(fileStream), "audio", Path.GetFileName(audioFilePath));
            if (minSpeakers.HasValue)
                content.Add(new StringContent(minSpeakers.Value.ToString()), "min_speakers");
            if (maxSpeakers.HasValue)
                content.Add(new StringContent(maxSpeakers.Value.ToString()), "max_speakers");

            using var response = await _httpClient.PostAsync(
                $"{_inferenceServiceUrl}{endpoint}",
                content,
                cancellationToken);

            var result = await DeserializeResponseAsync<DiarizationApiResponseDto>(response, cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException($"Diarization error: {result.ErrorMessage}");

            var normalizedSegments = NormalizeDiarizationSegments(result.Segments ?? []);
            var speakerCount = result.SpeakerCount > 0
                ? result.SpeakerCount
                : CountDistinctSpeakers(normalizedSegments);

            return new DiarizationResult(true, normalizedSegments, speakerCount, null);
        }
        catch (Exception ex)
        {
            _log.Error($"Diarization failed: {ex.Message}", ex);
            return new DiarizationResult(false, [], 0, ex.Message);
        }
    }

    /// <summary>
    /// Register an audio reference for a speaker with the Qwen TTS service.
    /// </summary>
    /// <param name="speakerId">Identifier of the speaker to associate with the uploaded reference audio.</param>
    /// <param name="referenceAudioPath">Path to the local audio file to upload as the reference.</param>
    /// <returns>The created reference identifier.</returns>
    /// <exception cref="FileNotFoundException">Thrown if <paramref name="referenceAudioPath"/> does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the service reports a failure or returns no reference ID.</exception>
    public async Task<string> RegisterQwenReferenceAsync(
        string speakerId,
        string referenceAudioPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(referenceAudioPath))
            throw new FileNotFoundException($"Reference audio file not found: {referenceAudioPath}");

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(speakerId), "speaker_id");

        await using var fs = File.OpenRead(referenceAudioPath);
        content.Add(new StreamContent(fs), "file", Path.GetFileName(referenceAudioPath));

        using var response = await _httpClient.PostAsync(
            $"{_inferenceServiceUrl}/tts/qwen/references",
            content,
            cancellationToken);

        var result = await DeserializeResponseAsync<QwenReferenceResponseDto>(response, cancellationToken);
        if (!result.Success || string.IsNullOrWhiteSpace(result.ReferenceId))
            throw new InvalidOperationException($"Qwen reference registration failed: {result.ErrorMessage}");

        return result.ReferenceId;
    }



    /// <summary>
    /// Synthesizes speech for the given text using the Qwen segmented TTS endpoint.
    /// </summary>
    /// <param name="text">The text to synthesize.</param>
    /// <param name="model">TTS model identifier; when null or whitespace the default model "Qwen/Qwen3-TTS-12Hz-1.7B-Base" is used.</param>
    /// <param name="language">Optional language tag (e.g., BCP-47) to guide synthesis.</param>
    /// <param name="referenceAudioPath">Path to a reference audio file to upload; the file is uploaded only when <paramref name="referenceId"/> is not provided.</param>
    /// <param name="referenceText">Optional textual description or transcript of the reference audio.</param>
    /// <param name="referenceId">Identifier of a previously registered reference; when provided, no reference file is uploaded.</param>
    /// <param name="cancellationToken">Cancellation token for the asynchronous operation.</param>
    /// <returns>
    /// A <see cref="TtsResult"/> containing the synthesis outcome: on success includes the remote audio path, voice identifier, and file size in bytes; on failure includes an error message and an empty audio path.
    /// </returns>
    public async Task<TtsResult> QwenSegmentAsync(
        string text,
        string model,
        string? language = null,
        string? referenceAudioPath = null,
        string? referenceText = null,
        string? referenceId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(text), "text");
            content.Add(new StringContent(string.IsNullOrWhiteSpace(model) ? "Qwen/Qwen3-TTS-12Hz-1.7B-Base" : model), "model");
            if (!string.IsNullOrWhiteSpace(language))
                content.Add(new StringContent(language), "language");
            if (!string.IsNullOrWhiteSpace(referenceText))
                content.Add(new StringContent(referenceText), "reference_text");
            if (!string.IsNullOrWhiteSpace(referenceId))
                content.Add(new StringContent(referenceId), "reference_id");

            FileStream? fs = null;
            try
            {
                if (string.IsNullOrWhiteSpace(referenceId) && !string.IsNullOrWhiteSpace(referenceAudioPath))
                {
                    if (!File.Exists(referenceAudioPath))
                        throw new FileNotFoundException($"Reference audio file not found: {referenceAudioPath}");
                    fs = File.OpenRead(referenceAudioPath);
                    content.Add(new StreamContent(fs), "reference_file", Path.GetFileName(referenceAudioPath));
                }

                using var response = await _httpClient.PostAsync(
                    $"{_inferenceServiceUrl}/tts/qwen/segment",
                    content,
                    cancellationToken);

                var result = await DeserializeResponseAsync<TtsApiResponseDto>(response, cancellationToken);
                if (!result.Success)
                    throw new InvalidOperationException($"Qwen TTS segment error: {result.ErrorMessage}");

                return new TtsResult(true, result.AudioPath ?? "", result.Voice ?? model, result.FileSizeBytes, null);
            }
            finally
            {
                fs?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Qwen TTS segment synthesis failed: {ex.Message}", ex);
            return new TtsResult(false, "", string.IsNullOrWhiteSpace(model) ? "Qwen/Qwen3-TTS-12Hz-1.7B-Base" : model, 0, ex.Message);
        }
    }

    /// <summary>
    /// Downloads synthesized TTS audio identified by the given filename from the inference service and writes it to the specified local file path.
    /// </summary>
    /// <param name="filename">The remote TTS audio filename on the service (will be URL-escaped).</param>
    /// <param name="localOutputPath">Path to create or overwrite with the downloaded audio file.</param>
    /// <param name="cancellationToken">Token to cancel the download operation.</param>
    /// <exception cref="InvalidOperationException">Thrown when the service responds with a non-success status; the exception message contains the response body.</exception>
    public async Task DownloadTtsAudioAsync(
        string filename,
        string localOutputPath,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"{_inferenceServiceUrl}/tts/audio/{Uri.EscapeDataString(filename)}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to download TTS audio '{filename}': {error}");
        }

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(
            localOutputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await contentStream.CopyToAsync(fileStream, cancellationToken);
    }

    /// <summary>
    /// Probes the containerized inference service for liveness, CUDA status, and a capabilities snapshot.
    /// </summary>
    /// <param name="httpClient">The HTTP client used to perform the probe requests.</param>
    /// <param name="serviceUrl">Base URL of the inference service to probe.</param>
    /// <param name="cancellationToken">Cancellation token to abort the probe requests.</param>
    /// <returns>
    /// A <see cref="ContainerHealthStatus"/> indicating whether the container is available. When available, includes CUDA availability/version and a capabilities snapshot; when unavailable, contains an error message describing the failure.
    /// </returns>
    private static async Task<ContainerHealthStatus> ProbeHealthAsync(
        HttpClient httpClient,
        string serviceUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var liveResponse = await httpClient.GetAsync(
                $"{serviceUrl}/health/live",
                cancellationToken).ConfigureAwait(false);
            var live = await DeserializeResponseAsync<LiveHealthResponseDto>(liveResponse, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(live.Status, "healthy", StringComparison.OrdinalIgnoreCase))
                return ContainerHealthStatus.Unavailable(serviceUrl, $"Unexpected live status '{live.Status ?? "unknown"}'.");

            ContainerCapabilitiesSnapshot capabilities;
            string? capabilitiesError = null;
            try
            {
                using var capabilitiesResponse = await httpClient.GetAsync(
                    $"{serviceUrl}/capabilities",
                    cancellationToken).ConfigureAwait(false);
                var capabilitiesDto = await DeserializeResponseAsync<CapabilitiesResponseDto>(capabilitiesResponse, cancellationToken).ConfigureAwait(false);
                capabilities = new ContainerCapabilitiesSnapshot(
                    capabilitiesDto.Transcription?.Ready ?? false,
                    capabilitiesDto.Transcription?.Detail,
                    capabilitiesDto.Translation?.Ready ?? false,
                    capabilitiesDto.Translation?.Detail,
                    capabilitiesDto.Tts?.Ready ?? false,
                    capabilitiesDto.Tts?.Detail,
                    capabilitiesDto.Tts?.Providers,
                    capabilitiesDto.Tts?.ProviderDetails,
                    capabilitiesDto.Diarization?.Ready ?? false,
                    capabilitiesDto.Diarization?.Detail,
                    NormalizeDiarizationProviderReadiness(capabilitiesDto.Diarization?.Providers),
                    NormalizeDiarizationProviderDetails(capabilitiesDto.Diarization?.ProviderDetails),
                    NormalizeDiarizationDefaultProvider(capabilitiesDto.Diarization?.DefaultProvider));
            }
            catch (Exception ex)
            {
                capabilitiesError = $"{CapabilitiesWarmupPrefix}: {ex.Message}";
                capabilities = CreateUnavailableCapabilitiesSnapshot(capabilitiesError);
            }

            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: live.CudaAvailable,
                CudaVersion: live.CudaVersion,
                ServiceUrl: serviceUrl,
                ErrorMessage: capabilitiesError,
                Capabilities: capabilities);
        }
        catch (Exception ex)
        {
            return ContainerHealthStatus.Unavailable(serviceUrl, ex.Message);
        }
    }

    /// <summary>
    /// Creates a capabilities snapshot where every capability stage is marked not ready and all detail fields contain the provided message.
    /// </summary>
    /// <param name="detail">A message describing why capabilities are unavailable; stored in each stage's detail field.</param>
    /// <returns>A <see cref="ContainerCapabilitiesSnapshot"/> with all stages set as not ready and their detail fields populated with <paramref name="detail"/>.</returns>
    private static ContainerCapabilitiesSnapshot CreateUnavailableCapabilitiesSnapshot(string detail) =>
        new(
            TranscriptionReady: false,
            TranscriptionDetail: detail,
            TranslationReady: false,
            TranslationDetail: detail,
            TtsReady: false,
            TtsDetail: detail,
            DiarizationReady: false,
            DiarizationDetail: detail);

    /// <summary>
    /// Deserialize an HTTP response JSON payload into an instance of <typeparamref name="T"/> and ensure the response indicates success.
    /// </summary>
    /// <param name="response">The HTTP response whose JSON body will be read and deserialized.</param>
    /// <param name="cancellationToken">Token to cancel reading the response body.</param>
    /// <returns>The deserialized object of type <typeparamref name="T"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the HTTP response status is not successful (message contains the response body or status code) or if JSON deserialization fails.</exception>
    private static async Task<T> DeserializeResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(payload)
                ? $"HTTP {(int)response.StatusCode}"
                : payload);

        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name} from service response.");
    }

    public static string NormalizeBaseUrl(string? serviceUrl)
    {
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return "http://localhost:8000";

        return serviceUrl.Trim().TrimEnd('/');
    }

    private sealed class LiveHealthResponseDto
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("cuda_available")]
        public bool CudaAvailable { get; set; }

        [JsonPropertyName("cuda_version")]
        public string? CudaVersion { get; set; }
    }

    private sealed class CapabilitiesResponseDto
    {
        [JsonPropertyName("transcription")]
        public StageCapabilityDto? Transcription { get; set; }

        [JsonPropertyName("translation")]
        public StageCapabilityDto? Translation { get; set; }

        [JsonPropertyName("tts")]
        public StageCapabilityDto? Tts { get; set; }

        [JsonPropertyName("diarization")]
        public StageCapabilityDto? Diarization { get; set; }
    }

    private sealed class StageCapabilityDto
    {
        [JsonPropertyName("ready")]
        public bool Ready { get; set; }

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("providers")]
        public Dictionary<string, bool>? Providers { get; set; }

        [JsonPropertyName("provider_details")]
        public Dictionary<string, string>? ProviderDetails { get; set; }

        [JsonPropertyName("default_provider")]
        public string? DefaultProvider { get; set; }
    }

    private sealed class TranscriptionApiResponseDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("language_probability")]
        public double LanguageProbability { get; set; }

        [JsonPropertyName("segments")]
        public List<TranscriptSegmentDto>? Segments { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private sealed class TranscriptSegmentDto
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private sealed class TranslationApiResponseDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("source_language")]
        public string? SourceLanguage { get; set; }

        [JsonPropertyName("target_language")]
        public string? TargetLanguage { get; set; }

        [JsonPropertyName("segments")]
        public List<TranslatedSegmentDto>? Segments { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private sealed class TranslatedSegmentDto
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("translated_text")]
        public string? TranslatedText { get; set; }

        [JsonPropertyName("speaker_id")]
        public string? SpeakerId { get; set; }
    }

    private sealed class TtsApiResponseDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("voice")]
        public string? Voice { get; set; }

        [JsonPropertyName("audio_path")]
        public string? AudioPath { get; set; }

        [JsonPropertyName("file_size_bytes")]
        public long FileSizeBytes { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private sealed class QwenReferenceResponseDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("reference_id")]
        public string? ReferenceId { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private sealed class DiarizationApiResponseDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("segments")]
        public List<DiarizationSegmentDto>? Segments { get; set; }

        [JsonPropertyName("speaker_count")]
        public int SpeakerCount { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private sealed class DiarizationSegmentDto
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }

        [JsonPropertyName("speaker_id")]
        public string? SpeakerId { get; set; }
    }

    /// <summary>
    /// Normalize a diarization engine identifier to a supported canonical provider id.
    /// </summary>
    /// <param name="engine">The input engine identifier; accepted values include legacy aliases and canonical provider IDs.</param>
    /// <returns><see cref="ProviderNames.WeSpeakerLocal"/> when the input resolves to WeSpeaker; otherwise <see cref="ProviderNames.NemoLocal"/>.</returns>
    private static string NormalizeDiarizationEngine(string engine) =>
        InferenceRuntimeCatalog.NormalizeDiarizationCapabilityProviderId(engine) switch
    {
        ProviderNames.WeSpeakerLocal => ProviderNames.WeSpeakerLocal,
        _ => ProviderNames.NemoLocal,
    };

    /// <summary>
    /// Normalize the keys of a diarization provider readiness map using the runtime catalog's provider ID normalization.
    /// </summary>
    /// <param name="providers">Mapping of provider identifiers to their readiness state; keys will be normalized.</param>
    /// <returns>
    /// A dictionary with normalized provider IDs mapped to the same readiness values, or the original <c>null</c> / empty input if none was provided.
    /// </returns>
    private static IReadOnlyDictionary<string, bool>? NormalizeDiarizationProviderReadiness(
        IReadOnlyDictionary<string, bool>? providers)
    {
        if (providers is null || providers.Count == 0)
            return providers;

        var normalized = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var pair in providers)
            normalized[InferenceRuntimeCatalog.NormalizeDiarizationCapabilityProviderId(pair.Key)] = pair.Value;
        return normalized;
    }

    /// <summary>
    /// Produces a copy of the provider-details dictionary with each key normalized to a canonical diarization provider ID.
    /// </summary>
    /// <param name="providerDetails">A mapping of provider identifier to detail string; may be null or empty.</param>
    /// <returns>
    /// A new dictionary whose keys are the normalized provider IDs and whose values are the original detail strings,
    /// or the original <paramref name="providerDetails"/> if it is null or empty.
    /// </returns>
    private static IReadOnlyDictionary<string, string>? NormalizeDiarizationProviderDetails(
        IReadOnlyDictionary<string, string>? providerDetails)
    {
        if (providerDetails is null || providerDetails.Count == 0)
            return providerDetails;

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in providerDetails)
            normalized[InferenceRuntimeCatalog.NormalizeDiarizationCapabilityProviderId(pair.Key)] = pair.Value;
        return normalized;
    }

    /// <summary>
    /// Normalizes a diarization default provider identifier for use with the runtime catalog.
    /// </summary>
    /// <param name="defaultProvider">The provider identifier to normalize; may be null or whitespace.</param>
    /// <returns>The normalized provider identifier, or the original <c>defaultProvider</c> value if it is null or whitespace.</returns>
    private static string? NormalizeDiarizationDefaultProvider(string? defaultProvider)
    {
        if (string.IsNullOrWhiteSpace(defaultProvider))
            return defaultProvider;

        return InferenceRuntimeCatalog.NormalizeDiarizationCapabilityProviderId(defaultProvider);
    }

    /// <summary>
    /// Normalize speaker identifiers for a collection of diarization segments.
    /// </summary>
    /// <param name="segments">The diarization segments whose speaker identifiers should be normalized; timings are preserved.</param>
    /// <returns>A list of DiarizedSegment objects with normalized speaker IDs and the original start/end times.</returns>
    private static IReadOnlyList<DiarizedSegment> NormalizeDiarizationSegments(
        IReadOnlyList<DiarizationSegmentDto> segments)
    {
        var normalized = new List<DiarizedSegment>(segments.Count);
        var assignedLabels = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var segment in segments)
        {
            normalized.Add(new DiarizedSegment(
                segment.Start,
                segment.End,
                NormalizeSpeakerId(segment.SpeakerId, assignedLabels)));
        }

        return normalized;
    }

    /// <summary>
    /// Normalize a raw speaker identifier into a consistent "spk_{NN}" label and record the mapping.
    /// </summary>
    /// <param name="rawSpeakerId">The raw speaker identifier which may be null, blank, or already normalized.</param>
    /// <param name="assignedLabels">A mapping of original keys to normalized speaker labels; this dictionary is updated with a new entry when a normalization is created.</param>
    /// <returns>The normalized speaker id in the form "spk_{NN}" where NN is a two-digit zero-based index.</returns>
    private static string NormalizeSpeakerId(string? rawSpeakerId, IDictionary<string, string> assignedLabels)
    {
        var key = string.IsNullOrWhiteSpace(rawSpeakerId)
            ? $"speaker_{assignedLabels.Count}"
            : rawSpeakerId.Trim();

        if (assignedLabels.TryGetValue(key, out var existing))
            return existing;

        var usedLabels = new HashSet<string>(assignedLabels.Values, StringComparer.Ordinal);
        var speakerIndex = assignedLabels.Count;
        string speakerId;
        do
        {
            speakerId = $"spk_{speakerIndex:D2}";
            speakerIndex++;
        }
        while (usedLabels.Contains(speakerId));

        assignedLabels[key] = speakerId;
        return speakerId;
    }

    /// <summary>
    /// Count distinct non-empty speaker identifiers present in the provided diarization segments.
    /// </summary>
    /// <returns>The number of unique, non-blank speaker IDs found in <paramref name="segments"/>.</returns>
    private static int CountDistinctSpeakers(IReadOnlyList<DiarizedSegment> segments)
    {
        var speakers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            if (!string.IsNullOrWhiteSpace(segment.SpeakerId))
                speakers.Add(segment.SpeakerId);
        }

        return speakers.Count;
    }

}

public enum ContainerCapabilityStage
{
    Transcription,
    Translation,
    Tts,
    Diarization,
}

public sealed record ContainerCapabilitiesSnapshot(
    bool TranscriptionReady,
    string? TranscriptionDetail,
    bool TranslationReady,
    string? TranslationDetail,
    bool TtsReady,
    string? TtsDetail,
    IReadOnlyDictionary<string, bool>? TtsProviders = null,
    IReadOnlyDictionary<string, string>? TtsProviderDetails = null,
    bool DiarizationReady = false,
    string? DiarizationDetail = null,
    IReadOnlyDictionary<string, bool>? DiarizationProviders = null,
    IReadOnlyDictionary<string, string>? DiarizationProviderDetails = null,
    string? DiarizationDefaultProvider = null)
{
    /// <summary>
    /// Indicates whether the given capability stage is ready.
    /// </summary>
    /// <param name="stage">The capability stage to query.</param>
    /// <returns>`true` if the specified stage is ready, `false` otherwise.</returns>
    public bool IsReady(ContainerCapabilityStage stage) => stage switch
    {
        ContainerCapabilityStage.Transcription => TranscriptionReady,
        ContainerCapabilityStage.Translation => TranslationReady,
        ContainerCapabilityStage.Tts => TtsReady,
        ContainerCapabilityStage.Diarization => DiarizationReady,
        _ => false,
    };

    /// <summary>
    /// Gets the detail message associated with the specified capability stage.
    /// </summary>
    /// <param name="stage">The capability stage to query.</param>
    /// <returns>The detail string for the stage, or `null` if no detail is available or the stage is unrecognized.</returns>
    public string? Detail(ContainerCapabilityStage stage) => stage switch
    {
        ContainerCapabilityStage.Transcription => TranscriptionDetail,
        ContainerCapabilityStage.Translation => TranslationDetail,
        ContainerCapabilityStage.Tts => TtsDetail,
        ContainerCapabilityStage.Diarization => DiarizationDetail,
        _ => null,
    };

    /// <summary>
    /// Checks whether readiness status or detail information exists for a given TTS provider identifier.
    /// </summary>
    /// <param name="providerId">The TTS provider identifier to look up; returns false if null or whitespace.</param>
    /// <param name="ready">Set to the provider's readiness value when present, otherwise false.</param>
    /// <param name="detail">Set to the provider's detail string when present, otherwise null.</param>
    /// <returns>`true` if either a readiness entry or a detail string was found for the given providerId, `false` otherwise.</returns>
    public bool TryGetTtsProviderReadiness(string providerId, out bool ready, out string? detail)
    {
        ready = false;
        detail = null;
        if (string.IsNullOrWhiteSpace(providerId))
            return false;

        var found = false;
        if (TtsProviders is not null && TtsProviders.TryGetValue(providerId, out var providerReady))
        {
            ready = providerReady;
            found = true;
        }

        if (TtsProviderDetails is not null && TtsProviderDetails.TryGetValue(providerId, out var providerDetail))
            detail = providerDetail;

        return found || detail is not null;
    }

    /// <summary>
    /// Attempts to retrieve the readiness and detail information for a diarization provider identified by <paramref name="providerId"/>.
    /// </summary>
    /// <param name="providerId">Normalized diarization provider identifier to look up.</param>
    /// <param name="ready">Set to the provider's readiness state if available; otherwise <c>false</c>.</param>
    /// <param name="detail">Set to the provider's detail string if available; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> if either readiness or detail information was found for the given provider; <c>false</c> if <paramref name="providerId"/> is null/blank or no information was found.</returns>
    public bool TryGetDiarizationProviderReadiness(string providerId, out bool ready, out string? detail)
    {
        ready = false;
        detail = null;
        if (string.IsNullOrWhiteSpace(providerId))
            return false;

        var found = false;
        if (DiarizationProviders is not null && DiarizationProviders.TryGetValue(providerId, out var providerReady))
        {
            ready = providerReady;
            found = true;
        }

        if (DiarizationProviderDetails is not null && DiarizationProviderDetails.TryGetValue(providerId, out var providerDetail))
            detail = providerDetail;

        return found || detail is not null;
    }
}

public sealed record ContainerHealthStatus(
    bool IsAvailable,
    bool CudaAvailable,
    string? CudaVersion,
    string ServiceUrl,
    string? ErrorMessage,
    ContainerCapabilitiesSnapshot? Capabilities = null)
{
    public static ContainerHealthStatus Unavailable(string url, string? reason = null) =>
        new(false, false, null, url, reason, null);

    public string StatusLine
    {
        get
        {
            if (!IsAvailable) return "Container unavailable";

            var cuda = CudaAvailable
                ? $"CUDA {CudaVersion ?? "✓"}"
                : "CPU-only";

            if (Capabilities is null)
                return $"Healthy ({cuda})";

            return $"Healthy ({cuda}) · tx={(Capabilities.TranscriptionReady ? "✓" : "x")} · tl={(Capabilities.TranslationReady ? "✓" : "x")} · tts={(Capabilities.TtsReady ? "✓" : "x")} · diar={(Capabilities.DiarizationReady ? "✓" : "x")}";
        }
    }
}
