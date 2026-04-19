using System;
using System.ComponentModel;
using System.Threading.Tasks;
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
    public bool HasTimingOverride => TimingModeOverride is not null && TimingModeOverride != SegmentTimingMode.Pause;

    /// <summary>Display label for the current per-segment timing mode.</summary>
    public string TimingOverrideLabel => TimingModeOverride switch
    {
        SegmentTimingMode.Off => "Off (override)",
        SegmentTimingMode.Stretch => "Stretch (override)",
        SegmentTimingMode.Pause => "Inherit",
        null => "Inherit",
        _ => "Unknown",
    };

    /// <summary>
    /// Initializes a new instance bound to the provided playback's preview and populates state from its currently selected segment.
    /// </summary>
    /// <param name="playback">The playback view model whose Preview provides the selected segment and change notifications.</param>
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

    /// <summary>
    /// Update the view model to reflect the provided segment; if <paramref name="segment"/> is null, clear and hide the view.
    /// </summary>
    /// <param name="segment">The segment state to display. If null, the view is hidden and segment-related properties are reset; otherwise the view is made visible and the view model properties are populated from the segment (including setting <see cref="SegmentId"/>, <see cref="SourceText"/>, <see cref="TranslatedText"/> as an empty string when null, <see cref="HasTranslation"/>, <see cref="HasTtsAudio"/>, <see cref="TimingModeOverride"/>, and <see cref="TimingLabel"/> formatted as "Start s → End s (duration s)" with one decimal place).</param>
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
    /// <summary>
    /// Applies a per-segment timing override to the currently inspected segment and propagates the change to the playback preview.
    /// </summary>
    /// <param name="mode">The timing mode to apply for the segment; passing <c>null</c> clears the per-segment override and reverts to the session-level setting.</param>
    [RelayCommand]
    private void SetTimingOverride(SegmentTimingMode? mode)
    {
        var currentId = SegmentId;
        if (string.IsNullOrEmpty(currentId))
            return;

        TimingModeOverride = mode;
        _preview.ApplySegmentTimingOverride(currentId, mode);
    }
    /// <summary>
    /// Detaches the view model's preview PropertyChanged handler and performs cleanup.
    /// </summary>
    /// <remarks>
    /// Unsubscribes OnPreviewPropertyChanged from _preview.PropertyChanged so the view model no longer receives preview updates.
    /// </remarks>
    public void Dispose()
    {
        _preview.PropertyChanged -= OnPreviewPropertyChanged;
    }
}
