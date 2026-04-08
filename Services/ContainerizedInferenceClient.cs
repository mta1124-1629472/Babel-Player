using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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
    /// Performs staged health checks against the inference service: verifies liveness and probes capabilities.
    /// </summary>
    /// <param name="httpClient">The HTTP client used to send probe requests.</param>
    /// <param name="serviceUrl">The service base URL (should be normalized, without a trailing slash).</param>
    /// <param name="cancellationToken">Token to cancel the probe operations.</param>
    /// <returns>
    /// A ContainerHealthStatus describing service availability. If the liveness check fails or an exception occurs, an unavailable status is returned with the error message; otherwise the result includes CUDA information and a capabilities snapshot (which may contain a warmup-prefixed error detail if capability probing failed).
    /// <summary>
    /// Probes the service's liveness and capabilities and returns a consolidated health status.
    /// </summary>
    /// <param name="httpClient">The HTTP client used to perform the probe requests.</param>
    /// <param name="serviceUrl">Base URL of the inference service to probe (should not include a trailing '/').</param>
    /// <param name="cancellationToken">Token to observe while waiting for the HTTP responses.</param>
    /// <returns>A ContainerHealthStatus indicating whether the container is available. When available, includes CUDA availability/version and a capabilities snapshot; when unavailable, contains an error message describing the failure.</returns>
    private static async Task<ContainerHealthStatus> ProbeHealthAsync(
        HttpClient httpClient,
        string serviceUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var liveResponse = await httpClient.GetAsync(
                $"{serviceUrl}/health/live",
                cancellationToken);
            var live = await DeserializeResponseAsync<LiveHealthResponseDto>(liveResponse, cancellationToken);
            if (!string.Equals(live.Status, "healthy", StringComparison.OrdinalIgnoreCase))
                return ContainerHealthStatus.Unavailable(serviceUrl, $"Unexpected live status '{live.Status ?? "unknown"}'.");

            ContainerCapabilitiesSnapshot capabilities;
            string? capabilitiesError = null;
            try
            {
                using var capabilitiesResponse = await httpClient.GetAsync(
                    $"{serviceUrl}/capabilities",
                    cancellationToken);
                var capabilitiesDto = await DeserializeResponseAsync<CapabilitiesResponseDto>(capabilitiesResponse, cancellationToken);
                capabilities = new ContainerCapabilitiesSnapshot(
                    capabilitiesDto.Transcription?.Ready ?? false,
                    capabilitiesDto.Transcription?.Detail,
                    capabilitiesDto.Translation?.Ready ?? false,
                    capabilitiesDto.Translation?.Detail,
                    capabilitiesDto.Tts?.Ready ?? false,
                    capabilitiesDto.Tts?.Detail,
                    capabilitiesDto.Tts?.Providers,
                    capabilitiesDto.Tts?.ProviderDetails);
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

    private static ContainerCapabilitiesSnapshot CreateUnavailableCapabilitiesSnapshot(string detail) =>
        new(
            TranscriptionReady: false,
            TranscriptionDetail: detail,
            TranslationReady: false,
            TranslationDetail: detail,
            TtsReady: false,
            TtsDetail: detail);

    private static async Task<T> DeserializeResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
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

}

public enum ContainerCapabilityStage
{
    Transcription,
    Translation,
    Tts,
}

public sealed record ContainerCapabilitiesSnapshot(
    bool TranscriptionReady,
    string? TranscriptionDetail,
    bool TranslationReady,
    string? TranslationDetail,
    bool TtsReady,
    string? TtsDetail,
    IReadOnlyDictionary<string, bool>? TtsProviders = null,
    IReadOnlyDictionary<string, string>? TtsProviderDetails = null)
{
    public bool IsReady(ContainerCapabilityStage stage) => stage switch
    {
        ContainerCapabilityStage.Transcription => TranscriptionReady,
        ContainerCapabilityStage.Translation => TranslationReady,
        ContainerCapabilityStage.Tts => TtsReady,
        _ => false,
    };

    public string? Detail(ContainerCapabilityStage stage) => stage switch
    {
        ContainerCapabilityStage.Transcription => TranscriptionDetail,
        ContainerCapabilityStage.Translation => TranslationDetail,
        ContainerCapabilityStage.Tts => TtsDetail,
        _ => null,
    };

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

            return $"Healthy ({cuda}) · tx={(Capabilities.TranscriptionReady ? "✓" : "x")} · tl={(Capabilities.TranslationReady ? "✓" : "x")} · tts={(Capabilities.TtsReady ? "✓" : "x")}";
        }
    }
}
