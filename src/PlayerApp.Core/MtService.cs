using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PlayerApp.Core;

public class MtService
{
    private static readonly HttpClient HttpClient = new()
    {
        BaseAddress = new Uri("https://api.openai.com/v1/")
    };

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
    private string? _openAiApiKey;
    private string _cloudModel = "gpt-5-mini";

    public string LoadedModelPath { get; private set; } = string.Empty;
    public bool UseCloudTranslation => !string.IsNullOrWhiteSpace(_openAiApiKey);

    public void LoadModel(string modelPath)
    {
        LoadedModelPath = modelPath;
    }

    public void ConfigureCloud(string? apiKey, string? model)
    {
        _openAiApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (!string.IsNullOrWhiteSpace(model))
        {
            _cloudModel = model.Trim();
        }
    }

    public static async Task ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, "models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI API key validation failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
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

        if (!UseCloudTranslation)
        {
            return Task.FromResult(Translate(normalized));
        }

        return _pendingTranslations.GetOrAdd(normalized, _ => TranslateWithCloudCoreAsync(normalized, cancellationToken));
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (!UseCloudTranslation)
        {
            return texts.Select(Translate).ToList();
        }

        var normalizedTexts = texts.Select(text => text?.Trim() ?? string.Empty).ToList();
        if (normalizedTexts.All(text => string.Equals(LanguageDetector.Detect(text), "en", StringComparison.OrdinalIgnoreCase)))
        {
            return normalizedTexts;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _openAiApiKey);

        var payload = new
        {
            model = _cloudModel,
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
                            text = JsonSerializer.Serialize(normalizedTexts)
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
            throw new InvalidOperationException($"OpenAI translation failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!TryGetOutputText(document.RootElement, out var outputText))
        {
            throw new InvalidOperationException("OpenAI translation returned no output text.");
        }

        try
        {
            var translated = JsonSerializer.Deserialize<List<string>>(outputText);
            if (translated is null || translated.Count != normalizedTexts.Count)
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

    private async Task<string> TranslateWithCloudCoreAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _openAiApiKey);

            var payload = new
            {
                model = _cloudModel,
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
                                text = "Translate subtitle text into natural English. Preserve line breaks. Return only the translated subtitle text with no notes or commentary."
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
                                text
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
            if (TryGetOutputText(document.RootElement, out var translated))
            {
                return translated;
            }

            throw new InvalidOperationException("OpenAI translation returned no output text.");
        }
        finally
        {
            _pendingTranslations.TryRemove(text, out _);
        }
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
