using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Babel.Player.Models;

public sealed class TranscriptArtifact
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("language_probability")]
    public double LanguageProbability { get; set; }

    [JsonPropertyName("segments")]
    public List<TranscriptSegmentArtifact>? Segments { get; set; }
}

public sealed class TranscriptSegmentArtifact
{
    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("speakerId")]
    public string? SpeakerId { get; set; }
}
