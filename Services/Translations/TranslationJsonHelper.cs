using System.Collections.Generic;
using System.Text.Json;

namespace Babel.Player.Services.Translations;

/// <summary>
/// Represents the deserialized JSON structure returned by translation providers.
/// </summary>
public sealed class TranslationJsonHelper
{
    public string? SourceLanguage { get; set; }
    public string? TargetLanguage { get; set; }
    public List<SegmentJsonHelper>? Segments { get; set; }

    public sealed class SegmentJsonHelper
    {
        public string? Id { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public string? Text { get; set; }
        public string? TranslatedText { get; set; }
    }

    /// <summary>
    /// Parses a JSON string into a <see cref="TranslationJsonHelper"/> instance.
    /// Returns null if deserialization fails.
    /// </summary>
    public static TranslationJsonHelper? Parse(string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        try
        {
            return JsonSerializer.Deserialize<TranslationJsonHelper>(json, options);
        }
        catch
        {
            return null;
        }
    }
}
