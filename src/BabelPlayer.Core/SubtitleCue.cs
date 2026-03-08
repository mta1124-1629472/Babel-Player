namespace BabelPlayer.Core;

public class SubtitleCue
{
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string SourceText { get; init; } = string.Empty;
    public string? SourceLanguage { get; set; }
    public string? TranslatedText { get; set; }

    public string DisplayText => string.IsNullOrWhiteSpace(TranslatedText) ? SourceText : TranslatedText;
}
