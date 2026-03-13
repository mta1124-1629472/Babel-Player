using BabelPlayer.App;

namespace BabelPlayer.WinUI;

public sealed class SubtitleOverlayViewModel : ObservableObject
{
    private string _sourceText = string.Empty;
    private string _translationText = "Open a local video. Babel Player will auto-generate local captions if no .srt is found.";
    private bool _showSource;
    private bool _isVisible = true;
    private string _statusText = string.Empty;
    private string _selectedTranscriptionLabel = SubtitleWorkflowCatalog.GetTranscriptionModel(SubtitleWorkflowCatalog.DefaultTranscriptionModelKey).DisplayName;
    private string _selectedTranslationLabel = SubtitleWorkflowCatalog.GetTranslationModel(null).DisplayName;
    private bool _isTranslationEnabled;
    private bool _isAutoTranslateEnabled;
    private SubtitlePipelineSource _subtitleSource;
    private bool _isCaptionGenerationInProgress;

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

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string SelectedTranscriptionLabel
    {
        get => _selectedTranscriptionLabel;
        set => SetProperty(ref _selectedTranscriptionLabel, value);
    }

    public string SelectedTranslationLabel
    {
        get => _selectedTranslationLabel;
        set => SetProperty(ref _selectedTranslationLabel, value);
    }

    public bool IsTranslationEnabled
    {
        get => _isTranslationEnabled;
        set => SetProperty(ref _isTranslationEnabled, value);
    }

    public bool IsAutoTranslateEnabled
    {
        get => _isAutoTranslateEnabled;
        set => SetProperty(ref _isAutoTranslateEnabled, value);
    }

    public SubtitlePipelineSource SubtitleSource
    {
        get => _subtitleSource;
        set => SetProperty(ref _subtitleSource, value);
    }

    public bool IsCaptionGenerationInProgress
    {
        get => _isCaptionGenerationInProgress;
        set => SetProperty(ref _isCaptionGenerationInProgress, value);
    }
}
