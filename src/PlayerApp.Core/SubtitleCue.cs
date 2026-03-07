namespace PlayerApp.Core;

public class SubtitleCue
{
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? TranslatedText { get; set; }
}
