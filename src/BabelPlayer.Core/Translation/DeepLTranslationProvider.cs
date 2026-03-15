using System.Net.Http.Headers;
using System.Text.Json;

namespace BabelPlayer.Core.Translation;

public sealed class DeepLTranslationProvider : ITranslationProvider
{
    private static readonly HttpClient Http = new();

    private readonly string _apiKey;

    public DeepLTranslationProvider(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey.Trim();
    }

    public string Name => "DeepL";

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var result = await TranslateBatchAsync(["hola"], cancellationToken);
        if (result.Count != 1 || string.IsNullOrWhiteSpace(result[0]))
            throw new InvalidOperationException("DeepL validation returned no result.");
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var baseUri = _apiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";

        var formPairs = new List<KeyValuePair<string, string>> { new("target_lang", "EN") };
        formPairs.AddRange(texts.Select(t => new KeyValuePair<string, string>("text", t)));

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUri)
        {
            Content = new FormUrlEncodedContent(formPairs)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _apiKey);

        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepL translation failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");

        using var doc = JsonDocument.Parse(body);
        var translations = doc.RootElement
            .GetProperty("translations")
            .EnumerateArray()
            .Select(item => item.GetProperty("text").GetString() ?? string.Empty)
            .ToList();

        if (translations.Count != texts.Count)
            throw new InvalidOperationException("DeepL translation returned an unexpected batch size.");

        return translations;
    }
}
