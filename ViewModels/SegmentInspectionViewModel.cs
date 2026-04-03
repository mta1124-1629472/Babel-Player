using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Babel.Player.Models;

namespace Babel.Player.ViewModels;

public partial class SegmentInspectionViewModel : ViewModelBase, IDisposable
{
    private readonly EmbeddedPlaybackViewModel _playback;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _segmentId = "";

    [ObservableProperty]
    private string _sourceText = "";

    [ObservableProperty]
    private string _translatedText = "";

    [ObservableProperty]
    private bool _hasTranslation;

    [ObservableProperty]
    private bool _hasTtsAudio;

    [ObservableProperty]
    private string _timingLabel = "";

    public SegmentInspectionViewModel(EmbeddedPlaybackViewModel playback)
    {
        _playback = playback;
        _playback.PropertyChanged += OnPlaybackPropertyChanged;
        Refresh(_playback.SelectedSegment);
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EmbeddedPlaybackViewModel.SelectedSegment))
        {
            Refresh(_playback.SelectedSegment);
        }
    }

    public void Refresh(WorkflowSegmentState? segment)
    {
        if (segment is null)
        {
            IsVisible = false;
            SegmentId = "";
            SourceText = "";
            TranslatedText = "";
            HasTranslation = false;
            HasTtsAudio = false;
            TimingLabel = "";
            return;
        }

        IsVisible = true;
        SegmentId = segment.SegmentId;
        SourceText = segment.SourceText;
        TranslatedText = segment.TranslatedText ?? "";
        HasTranslation = segment.HasTranslation;
        HasTtsAudio = segment.HasTtsAudio;

        var duration = segment.EndSeconds - segment.StartSeconds;
        TimingLabel = $"{segment.StartSeconds:F1}s → {segment.EndSeconds:F1}s ({duration:F1}s)";
    }

    public void Dispose()
    {
        _playback.PropertyChanged -= OnPlaybackPropertyChanged;
    }
}
