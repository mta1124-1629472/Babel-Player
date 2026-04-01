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

    public async Task<TranscriptionResult> TranscribeAsync(
        string audioFilePath,
        string modelName = "base",
        string? language = null,
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

    public async Task<TranslationResult> TranslateAsync(
        List<TranscriptSegment> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _log.Info($"Translating {sourceLanguage} -> {targetLanguage}");

            var transcriptJson = JsonSerializer.Serialize(new { segments });

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
