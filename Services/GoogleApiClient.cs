using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Lightweight HTTP client for Google Cloud API validation.
///
/// Uses the Cloud Text-to-Speech <c>voices.list</c> endpoint as a key probe:
/// a valid API key returns a 200 with the voice catalogue; an invalid/revoked key
/// returns 400 or 403. The endpoint is free to call and requires no payload.
/// </summary>
public sealed class GoogleApiClient : IDisposable
{
    private const string TtsBaseUrl = "https://texttospeech.googleapis.com/v1/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public GoogleApiClient(string apiKey, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key cannot be empty.", nameof(apiKey));

        _apiKey = apiKey.Trim();
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.BaseAddress = new Uri(TtsBaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BabelPlayer/1.0");
    }

    /// <summary>
    /// Probes the Cloud TTS <c>voices.list</c> endpoint to confirm the key is valid.
    /// Returns the number of voices available on success.
    /// </summary>
    public async Task<GoogleVoicesInfo> ListVoicesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"voices?key={Uri.EscapeDataString(_apiKey)}", cancellationToken);
        return await ReadJsonAsync<GoogleVoicesInfo>(response, cancellationToken);
    }

    public void Dispose() => _httpClient.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new GoogleApiException(response.StatusCode, ExtractErrorMessage(body));
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException("Received empty response from Google API.");
    }

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                return msg.GetString() ?? body;
        }
        catch { /* fall through */ }
        return string.IsNullOrWhiteSpace(body) ? "Unknown error" : body;
    }
}

public sealed record GoogleVoicesInfo(
    [property: JsonPropertyName("voices")] GoogleVoiceItem[]? Voices)
{
    public int Count => Voices?.Length ?? 0;
}

public sealed record GoogleVoiceItem(
    [property: JsonPropertyName("name")] string? Name);

public sealed class GoogleApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public GoogleApiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
