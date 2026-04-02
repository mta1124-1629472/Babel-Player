using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class ElevenLabsApiClient : IDisposable
{
    private const string BaseUrl = "https://api.elevenlabs.io/v1/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public ElevenLabsApiClient(string apiKey, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key cannot be empty.", nameof(apiKey));

        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.Timeout = TimeSpan.FromMinutes(2);
        _httpClient.DefaultRequestHeaders.Add("xi-api-key", apiKey.Trim());
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BabelPlayer/1.0");
    }

    /// <summary>
    /// Fetches subscription info. Used as the live key-validation probe —
    /// any successful response confirms the key is accepted.
    /// </summary>
    public async Task<ElevenLabsSubscriptionInfo> GetSubscriptionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("user/subscription", cancellationToken);
        return await ReadJsonAsync<ElevenLabsSubscriptionInfo>(response, cancellationToken);
    }

    /// <summary>
    /// Generates speech for <paramref name="text"/> using the given <paramref name="voiceId"/>
    /// (ElevenLabs character voice ID) and <paramref name="modelId"/> (quality tier, e.g.
    /// <c>eleven_multilingual_v2</c>). Returns raw MP3 bytes.
    /// </summary>
    public async Task<byte[]> TextToSpeechAsync(
        string text,
        string voiceId,
        string modelId = "eleven_multilingual_v2",
        CancellationToken cancellationToken = default)
    {
        var body = new TtsRequestDto { Text = text, ModelId = modelId };
        var content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");

        // Explicitly request MP3 to receive binary audio data.
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"text-to-speech/{voiceId}")
        {
            Content = content
        };
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

        using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public void Dispose() => _httpClient.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException("Received empty response from ElevenLabs API.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new ElevenLabsApiException(response.StatusCode, body);
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private sealed class TtsRequestDto
    {
        [JsonPropertyName("text")]
        public required string Text { get; init; }

        [JsonPropertyName("model_id")]
        public required string ModelId { get; init; }
    }
}

public sealed record ElevenLabsSubscriptionInfo(
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("character_count")] int CharacterCount,
    [property: JsonPropertyName("character_limit")] int CharacterLimit);

public sealed class ElevenLabsApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public ElevenLabsApiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
