namespace BabelPlayer.WinUI;

public sealed record SubtitlePresentationModel
{
    public bool IsVisible { get; init; }
    public string PrimaryText { get; init; } = string.Empty;
    public string SecondaryText { get; init; } = string.Empty;
}
