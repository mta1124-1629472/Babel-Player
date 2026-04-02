using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class OpenAiApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient _httpClient;

    public OpenAiApiClient(string apiKey, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key cannot be empty.", nameof(apiKey));

        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.BaseAddress = new Uri("https://api.openai.com/v1/");
        _httpClient.Timeout = TimeSpan.FromMinutes(2);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("models", cancellationToken);
        var payload = await ReadJsonAsync<ModelsResponseDto>(response, cancellationToken);

        return [..
            (payload.Data ?? [])
                .Select(model => model.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()];
    }

    public async Task<string> CreateChatCompletionAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var request = new ChatCompletionRequestDto
        {
            Model = model,
            Temperature = 0.2,
            Messages =
            [
                new ChatMessageDto { Role = "system", Content = systemPrompt },
                new ChatMessageDto { Role = "user", Content = userPrompt },
            ],
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("chat/completions", content, cancellationToken);
        var payload = await ReadJsonAsync<ChatCompletionResponseDto>(response, cancellationToken);

        var message = payload.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException("OpenAI returned an empty chat completion response.");

        return message;
    }

    public async Task<OpenAiTranscriptionPayload> TranscribeAudioAsync(
        string audioFilePath,
        string model,
        string? languageHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!System.IO.File.Exists(audioFilePath))
            throw new System.IO.FileNotFoundException($"Audio file not found: {audioFilePath}");

        using var content = new MultipartFormDataContent();

        var stream = System.IO.File.OpenRead(audioFilePath);
        var streamContent = new StreamContent(stream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(streamContent, "file", System.IO.Path.GetFileName(audioFilePath));
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        content.Add(new StringContent("segment"), "timestamp_granularities[]");
        if (!string.IsNullOrWhiteSpace(languageHint)
            && !string.Equals(languageHint, "auto", StringComparison.OrdinalIgnoreCase))
        {
            content.Add(new StringContent(languageHint), "language");
        }

        using var response = await _httpClient.PostAsync("audio/transcriptions", content, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateApiException(response.StatusCode, payload);

        var root = JsonNode.Parse(payload)?.AsObject()
            ?? throw new InvalidOperationException("Failed to parse OpenAI transcription response.");

        var language = root["language"]?.GetValue<string?>() ?? "unknown";
        var text = root["text"]?.GetValue<string?>() ?? string.Empty;
        var segments = new List<OpenAiSegment>();

        if (root["segments"] is JsonArray segmentsArray)
        {
            foreach (var node in segmentsArray)
            {
                if (node is not JsonObject segObj)
                    continue;

                var segText = segObj["text"]?.GetValue<string?>();
                if (string.IsNullOrWhiteSpace(segText))
                    continue;

                var start = segObj["start"]?.GetValue<double?>() ?? 0.0;
                var end = segObj["end"]?.GetValue<double?>() ?? start;
                if (end < start) end = start;

                segments.Add(new OpenAiSegment(start, end, segText));
            }
        }

        return new OpenAiTranscriptionPayload(language, text, segments);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateApiException(response.StatusCode, payload);

        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name} from OpenAI response.");
    }

    private static OpenAiApiException CreateApiException(HttpStatusCode statusCode, string payload)
    {
        try
        {
            var error = JsonSerializer.Deserialize<ErrorEnvelopeDto>(payload, JsonOptions)?.Error;
            if (!string.IsNullOrWhiteSpace(error?.Message))
                return new OpenAiApiException(error.Message, statusCode);
        }
        catch
        {
            // Fall through to generic error.
        }

        var message = string.IsNullOrWhiteSpace(payload)
            ? $"OpenAI request failed with HTTP {(int)statusCode}."
            : $"OpenAI request failed with HTTP {(int)statusCode}: {payload}";

        return new OpenAiApiException(message, statusCode);
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed class ModelsResponseDto
    {
        [JsonPropertyName("data")]
        public List<ModelDto>? Data { get; set; }
    }

    private sealed class ModelDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private sealed class ChatCompletionRequestDto
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<ChatMessageDto> Messages { get; set; } = [];

        [JsonPropertyName("temperature")]
        public double? Temperature { get; set; }
    }

    private sealed class ChatMessageDto
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed class ChatCompletionResponseDto
    {
        [JsonPropertyName("choices")]
        public List<ChoiceDto>? Choices { get; set; }
    }

    private sealed class ChoiceDto
    {
        [JsonPropertyName("message")]
        public ChatMessageDto? Message { get; set; }
    }

    private sealed class ErrorEnvelopeDto
    {
        [JsonPropertyName("error")]
        public ErrorDto? Error { get; set; }
    }

    private sealed class ErrorDto
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}

public sealed class OpenAiApiException : Exception
{
    public OpenAiApiException(string message, HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}

public sealed record OpenAiTranscriptionPayload(
    string Language,
    string Text,
    IReadOnlyList<OpenAiSegment> Segments);

public sealed record OpenAiSegment(double StartSeconds, double EndSeconds, string Text);