using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class ContainerizedInferenceClient
{
    private readonly HttpClient _httpClient;
    private readonly AppLog _log;
    private readonly string _inferenceServiceUrl;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ContainerizedInferenceClient(string inferenceServiceUrl, AppLog log)
    {
        _inferenceServiceUrl = inferenceServiceUrl ?? "http://localhost:8000";
        _log = log;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>
    /// Pings <c>GET /health</c> and returns a snapshot of container readiness.
    /// Never throws — failures are captured in <see cref="ContainerHealthStatus.IsAvailable"/>.
    /// </summary>
    public async Task<ContainerHealthStatus> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                $"{_inferenceServiceUrl}/health",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return ContainerHealthStatus.Unavailable(_inferenceServiceUrl);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var s)
                ? s.GetString() : null;
            var cudaAvailable = root.TryGetProperty("cuda_available", out var c)
                && c.GetBoolean();
            var cudaVersion = root.TryGetProperty("cuda_version", out var cv)
                && cv.ValueKind == JsonValueKind.String
                ? cv.GetString() : null;

            return new ContainerHealthStatus(
                IsAvailable:     status == "healthy",
                CudaAvailable:   cudaAvailable,
                CudaVersion:     cudaVersion,
                ServiceUrl:      _inferenceServiceUrl,
                ErrorMessage:    null);
        }
        catch (Exception ex)
        {
            return ContainerHealthStatus.Unavailable(_inferenceServiceUrl, ex.Message);
        }
    }

    /// <summary>
    /// Blocking health check intended for use on background threads
    /// (e.g. inside <c>GatherBootstrapWarmupData</c>).
    /// Uses a dedicated short-timeout client to avoid stalling startup.
    /// </summary>
    public static ContainerHealthStatus CheckHealth(string serviceUrl, int timeoutSeconds = 5)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            var response = http.GetAsync($"{serviceUrl.TrimEnd('/')}/health")
                              .GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
                return ContainerHealthStatus.Unavailable(serviceUrl);

            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var s)
                ? s.GetString() : null;
            var cudaAvailable = root.TryGetProperty("cuda_available", out var c)
                && c.GetBoolean();
            var cudaVersion = root.TryGetProperty("cuda_version", out var cv)
                && cv.ValueKind == JsonValueKind.String
                ? cv.GetString() : null;

            return new ContainerHealthStatus(
                IsAvailable:   status == "healthy",
                CudaAvailable: cudaAvailable,
                CudaVersion:   cudaVersion,
                ServiceUrl:    serviceUrl,
                ErrorMessage:  null);
        }
        catch (Exception ex)
        {
            return ContainerHealthStatus.Unavailable(serviceUrl, ex.Message);
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

            var response = await _httpClient.PostAsync(
                $"{_inferenceServiceUrl}/transcribe",
                content,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Transcription failed: {error}");
            }

            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TranscriptionApiResponse>(jsonContent, JsonOptions);

            if (!result?.Success ?? false)
                throw new InvalidOperationException($"Transcription error: {result?.ErrorMessage}");

            var segments = new List<TranscriptSegment>();
            if (result?.Segments != null)
            {
                foreach (var seg in result.Segments)
                {
                    if (!string.IsNullOrEmpty(seg.Text))
                        segments.Add(new TranscriptSegment(seg.Start, seg.End, seg.Text));
                }
            }

            _log.Info($"Transcription complete: {segments.Count} segments");

            return new TranscriptionResult(
                true,
                segments,
                result?.Language ?? "unknown",
                result?.LanguageProbability ?? 0.0,
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
    /// <paramref name="transcriptJson"/> must be the raw transcript artifact JSON
    /// (e.g. {"language":"es","segments":[{"start":0.0,"end":3.68,"text":"..."}]}).
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

            var content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("transcript_json", transcriptJson),
                new KeyValuePair<string, string>("source_language", sourceLanguage),
                new KeyValuePair<string, string>("target_language", targetLanguage)
            ]);

            var response = await _httpClient.PostAsync(
                $"{_inferenceServiceUrl}/translate",
                content,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Translation failed: {error}");
            }

            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TranslationApiResponse>(jsonContent, JsonOptions);

            if (!result?.Success ?? false)
                throw new InvalidOperationException($"Translation error: {result?.ErrorMessage}");

            var translatedSegments = new List<TranslatedSegment>();
            if (result?.Segments != null)
            {
                foreach (var seg in result.Segments)
                {
                    translatedSegments.Add(new TranslatedSegment(
                        seg.Start,
                        seg.End,
                        seg.Text ?? "",
                        seg.TranslatedText ?? ""));
                }
            }

            _log.Info($"Translation complete: {translatedSegments.Count} segments");

            return new TranslationResult(true, translatedSegments, sourceLanguage, targetLanguage, null);
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

            var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("text", text),
                new KeyValuePair<string, string>("voice", voice)
            ]);

            var response = await _httpClient.PostAsync(
                $"{_inferenceServiceUrl}/tts",
                content,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"TTS failed: {error}");
            }

            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TtsApiResponse>(jsonContent, JsonOptions);

            if (!result?.Success ?? false)
                throw new InvalidOperationException($"TTS error: {result?.ErrorMessage}");

            _log.Info($"TTS generation complete: {result?.FileSizeBytes} bytes");

            return new TtsResult(true, result?.AudioPath ?? "", result?.Voice ?? "", result?.FileSizeBytes ?? 0, null);
        }
        catch (Exception ex)
        {
            _log.Error($"TTS failed: {ex.Message}", ex);
            return new TtsResult(false, "", "", 0, ex.Message);
        }
    }

    /// <summary>
    /// Downloads a TTS audio file previously generated by <see cref="TextToSpeechAsync"/>
    /// and writes it to <paramref name="localOutputPath"/>.
    /// Use <c>Path.GetFileName(TtsResult.AudioPath)</c> as <paramref name="filename"/>.
    /// </summary>
    public async Task DownloadTtsAudioAsync(
        string filename,
        string localOutputPath,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
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

    private class TranscriptionApiResponse
    {
        public bool Success { get; set; }
        public string? Language { get; set; }
        public double LanguageProbability { get; set; }
        public List<TranscriptSegmentDto>? Segments { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private class TranscriptSegmentDto
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string? Text { get; set; }
    }

    private class TranslationApiResponse
    {
        public bool Success { get; set; }
        public string? SourceLanguage { get; set; }
        public string? TargetLanguage { get; set; }
        public List<TranslatedSegmentDto>? Segments { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private class TranslatedSegmentDto
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string? Text { get; set; }
        public string? TranslatedText { get; set; }
    }

    private class TtsApiResponse
    {
        public bool Success { get; set; }
        public string? Voice { get; set; }
        public string? AudioPath { get; set; }
        public long FileSizeBytes { get; set; }
        public string? ErrorMessage { get; set; }
    }
}

/// <summary>
/// Result of probing <c>GET /health</c> on the containerized inference service.
/// </summary>
public sealed record ContainerHealthStatus(
    bool IsAvailable,
    bool CudaAvailable,
    string? CudaVersion,
    string ServiceUrl,
    string? ErrorMessage)
{
    public static ContainerHealthStatus Unavailable(string url, string? reason = null) =>
        new(false, false, null, url, reason);

    /// <summary>Human-readable single line suitable for the bootstrap diagnostics panel.</summary>
    public string StatusLine
    {
        get
        {
            if (!IsAvailable) return "Container unavailable";
            var cuda = CudaAvailable
                ? $"CUDA {CudaVersion ?? "✓"}"
                : "CPU-only";
            return $"Healthy ({cuda})";
        }
    }
}
