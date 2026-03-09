namespace BabelPlayer.App;

public sealed class SubtitleOverlayViewModel : ObservableObject
{
    private string _sourceText = string.Empty;
    private string _translationText = "Open a local video. Babel Player will auto-generate local captions if no .srt is found.";
    private bool _showSource;
    private bool _isVisible = true;

    public string SourceText
    {
        get => _sourceText;
        set => SetProperty(ref _sourceText, value);
    }

    public string TranslationText
    {
        get => _translationText;
        set => SetProperty(ref _translationText, value);
    }

    public bool ShowSource
    {
        get => _showSource;
        set => SetProperty(ref _showSource, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}
