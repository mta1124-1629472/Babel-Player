using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Babel.Player.Models;

namespace Babel.Player.ViewModels;

public partial class SegmentInspectionViewModel : ViewModelBase, IDisposable
{
    private readonly EmbeddedPlaybackPreviewViewModel _preview;

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

    /// <summary>
    /// The effective per-segment timing override for the selected segment.
    /// Null means "use the session-level DubTimingMode setting".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimingOverrideLabel))]
    [NotifyPropertyChangedFor(nameof(HasTimingOverride))]
    private SegmentTimingMode? _timingModeOverride;

    /// <summary>True when a per-segment timing override is active (not inheriting session default).</summary>
    public bool HasTimingOverride => TimingModeOverride is not null;

    /// <summary>Display label for the current per-segment timing mode.</summary>
    public string TimingOverrideLabel => TimingModeOverride switch
    {
        SegmentTimingMode.Off => "Off (override)",
        SegmentTimingMode.Stretch => "Stretch (override)",
        SegmentTimingMode.Pause => "Pause (override)",
        null => "Inherit",
        _ => "Unknown",
    };

    public SegmentInspectionViewModel(EmbeddedPlaybackViewModel playback)
    {
        _preview = playback.Preview;
        _preview.PropertyChanged += OnPreviewPropertyChanged;
        Refresh(_preview.SelectedSegment);
    }

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EmbeddedPlaybackPreviewViewModel.SelectedSegment))
        {
            Refresh(_preview.SelectedSegment);
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
            TimingModeOverride = null;
            return;
        }

        IsVisible = true;
        SegmentId = segment.SegmentId;
        SourceText = segment.SourceText;
        TranslatedText = segment.TranslatedText ?? "";
        HasTranslation = segment.HasTranslation;
        HasTtsAudio = segment.HasTtsAudio;
        TimingModeOverride = segment.TimingModeOverride;

        var duration = segment.EndSeconds - segment.StartSeconds;
        TimingLabel = $"{segment.StartSeconds:F1}s → {segment.EndSeconds:F1}s ({duration:F1}s)";
    }

    /// <summary>
    /// Sets the per-segment timing mode override and updates the segment in the preview collection.
    /// Passing null clears the override (reverts to session-level setting).
    /// </summary>
    [RelayCommand]
    private void SetTimingOverride(SegmentTimingMode? mode)
    {
        var currentId = SegmentId;
        if (string.IsNullOrEmpty(currentId))
            return;

        TimingModeOverride = mode;
        _preview.ApplySegmentTimingOverride(currentId, mode);
    }

    public void Dispose()
    {
        _preview.PropertyChanged -= OnPreviewPropertyChanged;
    }
}
