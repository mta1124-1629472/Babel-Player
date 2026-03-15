using System.Net;
using System.Text.Json;

namespace BabelPlayer.Core.Translation;

public sealed class GoogleTranslationProvider : ITranslationProvider
{
    private static readonly HttpClient Http = new();

    private readonly string _apiKey;

    public GoogleTranslationProvider(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey.Trim();
    }

    public string Name => "Google Translate";

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var result = await TranslateBatchAsync(["hola"], cancellationToken);
        if (result.Count != 1 || string.IsNullOrWhiteSpace(result[0]))
            throw new InvalidOperationException("Google Translate validation returned no result.");
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var uri = $"https://translation.googleapis.com/language/translate/v2?key={Uri.EscapeDataString(_apiKey)}";
        var formPairs = new List<KeyValuePair<string, string>>
        {
            new("target", "en"),
            new("format", "text")
        };
        formPairs.AddRange(texts.Select(t => new KeyValuePair<string, string>("q", t)));

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(formPairs)
        };

        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google Translate failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");

        using var doc = JsonDocument.Parse(body);
        var translations = doc.RootElement
            .GetProperty("data")
            .GetProperty("translations")
            .EnumerateArray()
            .Select(item => WebUtility.HtmlDecode(item.GetProperty("translatedText").GetString() ?? string.Empty))
            .ToList();

        if (translations.Count != texts.Count)
            throw new InvalidOperationException("Google Translate returned an unexpected batch size.");

        return translations;
    }
}
