using System.Text;
using System.Text.Json;

namespace BabelPlayer.Core.Translation;

public sealed class MicrosoftTranslationProvider : ITranslationProvider
{
    private static readonly HttpClient Http = new();

    private readonly string _apiKey;
    private readonly string _region;

    public MicrosoftTranslationProvider(string apiKey, string region)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        _apiKey = apiKey.Trim();
        _region = region.Trim();
    }

    public string Name => $"Microsoft Translator ({_region})";

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var result = await TranslateBatchAsync(["hola"], cancellationToken);
        if (result.Count != 1 || string.IsNullOrWhiteSpace(result[0]))
            throw new InvalidOperationException("Microsoft Translator validation returned no result.");
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        const string targetLanguage = "en";
        var uri = $"https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to={Uri.EscapeDataString(targetLanguage)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);
        request.Headers.Add("Ocp-Apim-Subscription-Region", _region);

        var body = texts.Select(t => new Dictionary<string, string> { ["Text"] = t }).ToList();
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Microsoft Translator failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");

        using var doc = JsonDocument.Parse(responseBody);
        var translations = doc.RootElement
            .EnumerateArray()
            .Select(item => item.GetProperty("translations")[0].GetProperty("text").GetString() ?? string.Empty)
            .ToList();

        if (translations.Count != texts.Count)
            throw new InvalidOperationException("Microsoft Translator returned an unexpected batch size.");

        return translations;
    }
}
