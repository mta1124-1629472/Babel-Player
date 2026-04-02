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
    private readonly HttpClient _httpClient;
    private readonly AppLog _log;
    private readonly string _inferenceServiceUrl;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };

    public ContainerizedInferenceClient(string inferenceServiceUrl, AppLog log)
    {
        _inferenceServiceUrl = NormalizeBaseUrl(inferenceServiceUrl);
        _log = log;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
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
        CancellationToken cancellationToken = default)
    {
        try
        {
            _log.Info($"Translating {sourceLanguage} -> {targetLanguage}");

            using var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("transcript_json", transcriptJson),
                new KeyValuePair<string, string>("source_language", sourceLanguage),
                new KeyValuePair<string, string>("target_language", targetLanguage)
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
                    seg.TranslatedText ?? string.Empty));
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

    public async Task DownloadTtsAudioAsync(
        string filename,
        string localOutputPath,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"{_inferenceServiceUrl}/tts/audio/{Uri.EscapeDataString(filename)}",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to download TTS audio '{filename}': {error}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await File.WriteAllBytesAsync(localOutputPath, bytes, cancellationToken);
    }

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

            using var capabilitiesResponse = await httpClient.GetAsync(
                $"{serviceUrl}/capabilities",
                cancellationToken);
            var capabilities = await DeserializeResponseAsync<CapabilitiesResponseDto>(capabilitiesResponse, cancellationToken);

            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: live.CudaAvailable,
                CudaVersion: live.CudaVersion,
                ServiceUrl: serviceUrl,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(
                    capabilities.Transcription?.Ready ?? false,
                    capabilities.Transcription?.Detail,
                    capabilities.Translation?.Ready ?? false,
                    capabilities.Translation?.Detail,
                    capabilities.Tts?.Ready ?? false,
                    capabilities.Tts?.Detail));
        }
        catch (Exception ex)
        {
            return ContainerHealthStatus.Unavailable(serviceUrl, ex.Message);
        }
    }

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
    string? TtsDetail)
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
