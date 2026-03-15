using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BabelPlayer.Core.Translation;

public sealed class OpenAiTranslationProvider : ITranslationProvider
{
    private static readonly HttpClient Http = new();

    private readonly string _apiKey;
    private readonly string _model;

    public OpenAiTranslationProvider(string apiKey, string? model = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey.Trim();
        _model  = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model.Trim();
    }

    public string Name => $"OpenAI ({_model})";

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI API key validation failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var payload = new
        {
            model = _model,
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

        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI translation failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");

        using var doc = JsonDocument.Parse(body);
        if (!TryGetOutputText(doc.RootElement, out var outputText))
            throw new InvalidOperationException("OpenAI translation returned no output text.");

        try
        {
            var translated = JsonSerializer.Deserialize<List<string>>(outputText);
            if (translated is null || translated.Count != texts.Count)
                throw new InvalidOperationException("OpenAI translation returned an unexpected batch size.");
            return translated;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("OpenAI translation returned malformed JSON.", ex);
        }
    }

    private static bool TryGetOutputText(JsonElement root, out string text)
    {
        text = string.Empty;

        if (root.TryGetProperty("output_text", out var direct))
        {
            var v = direct.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(v)) { text = v; return true; }
        }

        if (!root.TryGetProperty("output", out var outputArray) || outputArray.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var output in outputArray.EnumerateArray())
        {
            if (!output.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var content in contentArray.EnumerateArray())
            {
                if (!content.TryGetProperty("text", out var textEl)) continue;
                var v = textEl.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(v)) { text = v; return true; }
            }
        }

        return false;
    }
}
