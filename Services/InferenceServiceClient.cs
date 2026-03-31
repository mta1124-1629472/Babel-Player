using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Babel.Player.Services;

/// <summary>
/// HTTP client for remote GPU inference service.
/// Connects to containerized Python FastAPI service for transcription, translation, and TTS.
/// </summary>
public sealed class InferenceServiceClient : IDisposable
{
    private readonly AppLog _log;
    private readonly string _baseUrl;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public InferenceServiceClient(AppLog log, string serviceUrl = "http://localhost:8000")
    {
        _log = log;
        _baseUrl = serviceUrl.TrimEnd('/');
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        _log.Info($"Initialized InferenceServiceClient connecting to {_baseUrl}");
    }

    /// <summary>
    /// Check if inference service is healthy and reachable.
    /// </summary>
    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/health");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.Warning($"Inference service health check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Transcribe audio file using remote Whisper service.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAsync(
        string audioPath, string model = "base", string? language = null)
    {
        try
        {
            if (!System.IO.File.Exists(audioPath))
            {
                return new TranscriptionResult(
                    false, [], "unknown", 0, 
                    $"Audio file not found: {audioPath}");
            }

            _log.Info($"Sending transcription request to {_baseUrl}: {audioPath} (model: {model})");

            using var content = new MultipartFormDataContent();
            var fileBytes = await System.IO.File.ReadAllBytesAsync(audioPath);
            content.Add(new ByteArrayContent(fileBytes), "file", System.IO.Path.GetFileName(audioPath));
            content.Add(new StringContent(model), "model");
            
            if (!string.IsNullOrEmpty(language))
            {
                content.Add(new StringContent(language), "language");
            }

            var response = await _httpClient.PostAsync($"{_baseUrl}/transcribe", content);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _log.Error("Transcription failed", new Exception(responseJson));
                return new TranscriptionResult(
                    false, [], "unknown", 0, responseJson);
            }

            var result = JsonSerializer.Deserialize<RemoteTranscriptionResponse>(responseJson, _jsonOptions);
            if (result?.Success != true)
            {
                return new TranscriptionResult(
                    false, [], result?.Language ?? "unknown", 0,
                    result?.ErrorMessage ?? "Unknown error");
            }

            var segments = new List<TranscriptSegment>();
            foreach (var seg in (result.Segments ?? []))
            {
                if (!string.IsNullOrEmpty(seg.Text))
                {
                    segments.Add(new TranscriptSegment(seg.Start, seg.End, seg.Text));
                }
            }

            _log.Info($"Transcription successful: {segments.Count} segments, language: {result.Language}");

            return new TranscriptionResult(
                true, segments, result.Language ?? "unknown",
                result.LanguageProbability ?? 0.0, null);
        }
        catch (Exception ex)
        {
            _log.Error($"Transcription request failed: {ex.Message}", ex);
            return new TranscriptionResult(
                false, [], "unknown", 0, ex.Message);
        }
    }

    /// <summary>
    /// Translate transcript segments using remote service.
    /// </summary>
    public async Task<TranslationResult> TranslateAsync(
        string transcriptJson, string sourceLanguage, string targetLanguage)
    {
        try
        {
            _log.Info($"Sending translation request to {_baseUrl}: {sourceLanguage} -> {targetLanguage}");

            var request = new { transcript_json = transcriptJson, sourceLanguage, targetLanguage };
            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_baseUrl}/translate", content);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _log.Error("Translation failed", new Exception(responseJson));
                return new TranslationResult(
                    false, [], sourceLanguage, targetLanguage, responseJson);
            }

            var result = JsonSerializer.Deserialize<RemoteTranslationResponse>(responseJson, _jsonOptions);
            if (result?.Success != true)
            {
                return new TranslationResult(
                    false, [], sourceLanguage, targetLanguage,
                    result?.ErrorMessage ?? "Unknown error");
            }

            var segments = new List<TranslatedSegment>();
            foreach (var seg in (result.Segments ?? []))
            {
                segments.Add(new TranslatedSegment(
                    seg.Start, seg.End, seg.Text ?? "", seg.TranslatedText ?? ""));
            }

            _log.Info($"Translation successful: {segments.Count} segments translated");

            return new TranslationResult(true, segments, sourceLanguage, targetLanguage, null);
        }
        catch (Exception ex)
        {
            _log.Error($"Translation request failed: {ex.Message}", ex);
            return new TranslationResult(
                false, [], sourceLanguage, targetLanguage, ex.Message);
        }
    }

    /// <summary>
    /// Generate speech from text using remote TTS service.
    /// </summary>
    public async Task<TtsResult> GenerateTtsAsync(string text, string voice = "en-US-AriaNeural")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new TtsResult(false, "", voice, 0, "Text cannot be empty");
            }

            _log.Info($"Sending TTS request to {_baseUrl}: voice={voice}");

            var request = new { text, voice };
            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_baseUrl}/tts", content);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _log.Error("TTS generation failed", new Exception(responseJson));
                return new TtsResult(false, "", voice, 0, responseJson);
            }

            var result = JsonSerializer.Deserialize<RemoteTtsResponse>(responseJson, _jsonOptions);
            if (result?.Success != true)
            {
                return new TtsResult(false, "", voice, 0, result?.ErrorMessage ?? "Unknown error");
            }

            // Download the audio file
            var audioBytes = await _httpClient.GetByteArrayAsync(
                $"{_baseUrl}/tts/audio/{System.IO.Path.GetFileName(result.AudioPath)}");
            var localAudioPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"tts_{Guid.NewGuid():N}.mp3");
            await System.IO.File.WriteAllBytesAsync(localAudioPath, audioBytes);

            _log.Info($"TTS generation successful: {result.FileSizeBytes} bytes -> {localAudioPath}");

            return new TtsResult(true, localAudioPath, voice, result.FileSizeBytes, null);
        }
        catch (Exception ex)
        {
            _log.Error($"TTS request failed: {ex.Message}", ex);
            return new TtsResult(false, "", voice, 0, ex.Message);
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    // Remote service response models
    private class RemoteTranscriptionResponse
    {
        public bool Success { get; set; }
        public string? Language { get; set; }
        public double? LanguageProbability { get; set; }
        public List<RemoteSegment>? Segments { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private class RemoteSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string? Text { get; set; }
    }

    private class RemoteTranslationResponse
    {
        public bool Success { get; set; }
        public string? SourceLanguage { get; set; }
        public string? TargetLanguage { get; set; }
        public List<RemoteTranslatedSegment>? Segments { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private class RemoteTranslatedSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string? Text { get; set; }
        public string? TranslatedText { get; set; }
    }

    private class RemoteTtsResponse
    {
        public bool Success { get; set; }
        public string? Voice { get; set; }
        public string? AudioPath { get; set; }
        public long FileSizeBytes { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
