using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PlayerApp.Core;

public enum CloudTranslationProvider
{
    None,
    OpenAi,
    Google,
    DeepL,
    MicrosoftTranslator
}

public sealed record CloudTranslationOptions(
    CloudTranslationProvider Provider,
    string ApiKey,
    string? Model = null,
    string? Region = null);

public class MtService
{
    private static readonly HttpClient HttpClient = new();

    private readonly Dictionary<string, string> _phrasebook = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hola"] = "hello",
        ["gracias"] = "thank you",
        ["adios"] = "goodbye",
        ["buenos dias"] = "good morning",
        ["bonjour"] = "hello",
        ["merci"] = "thank you",
        ["au revoir"] = "goodbye",
        ["こんにちは"] = "hello",
        ["ありがとう"] = "thank you",
        ["你好"] = "hello",
        ["谢谢"] = "thank you",
        ["привет"] = "hello",
        ["спасибо"] = "thank you",
        ["مرحبا"] = "hello",
        ["شكرا"] = "thank you"
    };

    private readonly ConcurrentDictionary<string, Task<string>> _pendingTranslations = new(StringComparer.Ordinal);
    private CloudTranslationOptions? _cloudOptions;

    public string LoadedModelPath { get; private set; } = string.Empty;
    public bool UseCloudTranslation => _cloudOptions is not null && !string.IsNullOrWhiteSpace(_cloudOptions.ApiKey);
    public CloudTranslationProvider CloudProvider => _cloudOptions?.Provider ?? CloudTranslationProvider.None;

    public void LoadModel(string modelPath)
    {
        LoadedModelPath = modelPath;
    }

    public void ConfigureCloud(CloudTranslationOptions? options)
    {
        _cloudOptions = string.IsNullOrWhiteSpace(options?.ApiKey)
            ? null
            : options with { ApiKey = options.ApiKey.Trim() };
    }

    public static async Task ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI API key validation failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    public static async Task ValidateTranslationProviderAsync(CloudTranslationOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var result = await TranslateBatchWithProviderAsync(options, ["hola"], "en", cancellationToken);
        if (result.Count != 1 || string.IsNullOrWhiteSpace(result[0]))
        {
            throw new InvalidOperationException("Translation provider validation returned no result.");
        }
    }

    public string Translate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Trim();
        if (string.Equals(LanguageDetector.Detect(normalized), "en", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var translated = normalized;
        foreach (var pair in _phrasebook)
        {
            translated = translated.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
        }

        return translated == normalized ? $"[AI->EN] {normalized}" : translated;
    }

    public Task<string> TranslateAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(string.Empty);
        }

        var normalized = text.Trim();
        if (string.Equals(LanguageDetector.Detect(normalized), "en", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(normalized);
        }

        if (!UseCloudTranslation || _cloudOptions is null)
        {
            return Task.FromResult(Translate(normalized));
        }

        var cacheKey = $"{_cloudOptions.Provider}:{_cloudOptions.Model}:{normalized}";
        return _pendingTranslations.GetOrAdd(cacheKey, _ => TranslateWithCloudCoreAsync(cacheKey, normalized, _cloudOptions, cancellationToken));
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (!UseCloudTranslation || _cloudOptions is null)
        {
            return texts.Select(Translate).ToList();
        }

        var normalizedTexts = texts.Select(text => text?.Trim() ?? string.Empty).ToList();
        if (normalizedTexts.All(text => string.Equals(LanguageDetector.Detect(text), "en", StringComparison.OrdinalIgnoreCase)))
        {
            return normalizedTexts;
        }

        return await TranslateBatchWithProviderAsync(_cloudOptions, normalizedTexts, "en", cancellationToken);
    }

    private async Task<string> TranslateWithCloudCoreAsync(string cacheKey, string text, CloudTranslationOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var translated = await TranslateBatchWithProviderAsync(options, [text], "en", cancellationToken);
            return translated[0];
        }
        finally
        {
            _pendingTranslations.TryRemove(cacheKey, out _);
        }
    }

    private static Task<IReadOnlyList<string>> TranslateBatchWithProviderAsync(
        CloudTranslationOptions options,
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        return options.Provider switch
        {
            CloudTranslationProvider.OpenAi => TranslateWithOpenAiBatchAsync(options, texts, cancellationToken),
            CloudTranslationProvider.Google => TranslateWithGoogleBatchAsync(options, texts, targetLanguage, cancellationToken),
            CloudTranslationProvider.DeepL => TranslateWithDeepLBatchAsync(options, texts, cancellationToken),
            CloudTranslationProvider.MicrosoftTranslator => TranslateWithMicrosoftBatchAsync(options, texts, targetLanguage, cancellationToken),
            _ => Task.FromResult<IReadOnlyList<string>>(texts.ToList())
        };
    }

    private static async Task<IReadOnlyList<string>> TranslateWithOpenAiBatchAsync(
        CloudTranslationOptions options,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(options.Model) ? "gpt-5-mini" : options.Model,
            store = false,
            input = new object[]
            {
                new
                {
                    role = "system",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "Translate each subtitle item into natural English. Return only a JSON array of strings in the same order as the input. Preserve line breaks inside each item. Do not add commentary or markdown."
                        }
                    }
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = JsonSerializer.Serialize(texts)
                        }
                    }
                }
            }
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI translation failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!TryGetOutputText(document.RootElement, out var outputText))
        {
            throw new InvalidOperationException("OpenAI translation returned no output text.");
        }

        try
        {
            var translated = JsonSerializer.Deserialize<List<string>>(outputText);
            if (translated is null || translated.Count != texts.Count)
            {
                throw new InvalidOperationException("OpenAI translation returned an unexpected batch size.");
            }

            return translated;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("OpenAI translation returned malformed JSON.", ex);
        }
    }

    private static async Task<IReadOnlyList<string>> TranslateWithGoogleBatchAsync(
        CloudTranslationOptions options,
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var uri = $"https://translation.googleapis.com/language/translate/v2?key={Uri.EscapeDataString(options.ApiKey)}";
        var formPairs = new List<KeyValuePair<string, string>>
        {
            new("target", targetLanguage),
            new("format", "text")
        };

        formPairs.AddRange(texts.Select(text => new KeyValuePair<string, string>("q", text)));

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(formPairs)
        };

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Translate failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var translations = document.RootElement
            .GetProperty("data")
            .GetProperty("translations")
            .EnumerateArray()
            .Select(item => WebUtility.HtmlDecode(item.GetProperty("translatedText").GetString() ?? string.Empty))
            .ToList();

        if (translations.Count != texts.Count)
        {
            throw new InvalidOperationException("Google Translate returned an unexpected batch size.");
        }

        return translations;
    }

    private static async Task<IReadOnlyList<string>> TranslateWithDeepLBatchAsync(
        CloudTranslationOptions options,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var baseUri = options.ApiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";

        var formPairs = new List<KeyValuePair<string, string>>
        {
            new("target_lang", "EN")
        };

        formPairs.AddRange(texts.Select(text => new KeyValuePair<string, string>("text", text)));

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUri)
        {
            Content = new FormUrlEncodedContent(formPairs)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", options.ApiKey);

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DeepL translation failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var translations = document.RootElement
            .GetProperty("translations")
            .EnumerateArray()
            .Select(item => item.GetProperty("text").GetString() ?? string.Empty)
            .ToList();

        if (translations.Count != texts.Count)
        {
            throw new InvalidOperationException("DeepL translation returned an unexpected batch size.");
        }

        return translations;
    }

    private static async Task<IReadOnlyList<string>> TranslateWithMicrosoftBatchAsync(
        CloudTranslationOptions options,
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Region))
        {
            throw new InvalidOperationException("Microsoft Translator requires a region.");
        }

        var uri = $"https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to={Uri.EscapeDataString(targetLanguage)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Ocp-Apim-Subscription-Key", options.ApiKey);
        request.Headers.Add("Ocp-Apim-Subscription-Region", options.Region.Trim());

        var body = texts.Select(text => new Dictionary<string, string> { ["Text"] = text }).ToList();
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Microsoft Translator failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var translations = document.RootElement
            .EnumerateArray()
            .Select(item => item.GetProperty("translations")[0].GetProperty("text").GetString() ?? string.Empty)
            .ToList();

        if (translations.Count != texts.Count)
        {
            throw new InvalidOperationException("Microsoft Translator returned an unexpected batch size.");
        }

        return translations;
    }

    private static bool TryGetOutputText(JsonElement root, out string text)
    {
        text = string.Empty;

        if (root.TryGetProperty("output_text", out var outputTextElement))
        {
            var outputText = outputTextElement.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(outputText))
            {
                text = outputText;
                return true;
            }
        }

        if (!root.TryGetProperty("output", out var outputArray) || outputArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var output in outputArray.EnumerateArray())
        {
            if (!output.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var content in contentArray.EnumerateArray())
            {
                if (!content.TryGetProperty("text", out var textElement))
                {
                    continue;
                }

                var value = textElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    text = value;
                    return true;
                }
            }
        }

        return false;
    }
}
