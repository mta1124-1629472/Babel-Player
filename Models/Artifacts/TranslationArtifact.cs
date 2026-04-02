using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Babel.Player.Models;

public sealed class TranslationArtifact
{
    [JsonPropertyName("sourceLanguage")]
    public string? SourceLanguage { get; set; }

    [JsonPropertyName("targetLanguage")]
    public string? TargetLanguage { get; set; }

    [JsonPropertyName("segments")]
    public List<TranslationSegmentArtifact>? Segments { get; set; }
}

public sealed class TranslationSegmentArtifact
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("translatedText")]
    public string? TranslatedText { get; set; }

    [JsonPropertyName("speakerId")]
    public string? SpeakerId { get; set; }
}
