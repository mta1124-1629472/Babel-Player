using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class DeepLApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient _httpClient;

    public DeepLApiClient(string apiKey, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key cannot be empty.", nameof(apiKey));

        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.BaseAddress = new Uri(ResolveBaseUrl(apiKey));
        _httpClient.Timeout = TimeSpan.FromMinutes(2);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey.Trim());
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BabelPlayer/1.0");
    }

    public async Task<DeepLUsageInfo> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("usage", cancellationToken);
        var payload = await ReadJsonAsync<UsageResponseDto>(response, cancellationToken);

        return new DeepLUsageInfo(payload.CharacterCount, payload.CharacterLimit);
    }

    public async Task<IReadOnlyList<DeepLTranslationItem>> TranslateTextsAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
            return [];

        var normalizedTarget = NormalizeLanguage(targetLanguage, isTargetLanguage: true)
            ?? throw new ArgumentException("Target language cannot be empty.", nameof(targetLanguage));

        var request = new TranslateRequestDto
        {
            Text = [.. texts],
            TargetLanguage = normalizedTarget
        };

        var normalizedSource = NormalizeLanguage(sourceLanguage, isTargetLanguage: false);
        if (!string.IsNullOrWhiteSpace(normalizedSource))
            request.SourceLanguage = normalizedSource;

        var content = new StringContent(
            JsonSerializer.Serialize(request, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.PostAsync("translate", content, cancellationToken);
        var payload = await ReadJsonAsync<TranslateResponseDto>(response, cancellationToken);

        return [..
            (payload.Translations ?? [])
                .Select(item => new DeepLTranslationItem(
                    item.Text ?? string.Empty,
                    item.DetectedSourceLanguage ?? string.Empty))];
    }

    private static string ResolveBaseUrl(string apiKey)
    {
        var trimmed = apiKey.Trim();
        return trimmed.Contains(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/"
            : "https://api.deepl.com/v2/";
    }

    private static string? NormalizeLanguage(string? languageCode, bool isTargetLanguage)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return null;

        var normalized = languageCode.Trim().Replace('_', '-').ToUpperInvariant();
        if (normalized == "AUTO")
            return null;

        if (isTargetLanguage)
        {
            if (normalized is "EN-US" or "EN-GB" or "PT-BR" or "PT-PT")
                return normalized;

            var dash = normalized.IndexOf('-');
            if (dash > 0)
                return normalized[..dash];

            return normalized;
        }

        var sourceDash = normalized.IndexOf('-');
        return sourceDash > 0 ? normalized[..sourceDash] : normalized;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateApiException(response.StatusCode, payload);

        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name} from DeepL response.");
    }

    private static DeepLApiException CreateApiException(HttpStatusCode statusCode, string payload)
    {
        var message = string.IsNullOrWhiteSpace(payload)
            ? $"DeepL request failed with HTTP {(int)statusCode}."
            : $"DeepL request failed with HTTP {(int)statusCode}: {payload}";

        return new DeepLApiException(message, statusCode);
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed class UsageResponseDto
    {
        [JsonPropertyName("character_count")]
        public long CharacterCount { get; set; }

        [JsonPropertyName("character_limit")]
        public long CharacterLimit { get; set; }
    }

    private sealed class TranslateRequestDto
    {
        [JsonPropertyName("text")]
        public List<string> Text { get; set; } = [];

        [JsonPropertyName("target_lang")]
        public string TargetLanguage { get; set; } = string.Empty;

        [JsonPropertyName("source_lang")]
        public string? SourceLanguage { get; set; }
    }

    private sealed class TranslateResponseDto
    {
        [JsonPropertyName("translations")]
        public List<TranslateItemDto>? Translations { get; set; }
    }

    private sealed class TranslateItemDto
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("detected_source_language")]
        public string? DetectedSourceLanguage { get; set; }
    }
}

public sealed class DeepLApiException : Exception
{
    public DeepLApiException(string message, HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}

public sealed record DeepLUsageInfo(long CharacterCount, long CharacterLimit);

public sealed record DeepLTranslationItem(string Text, string DetectedSourceLanguage);