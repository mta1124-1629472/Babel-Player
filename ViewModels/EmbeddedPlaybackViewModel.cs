using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Babel.Deck.Models;
using Babel.Deck.Services;

namespace Babel.Deck.ViewModels;

public partial class EmbeddedPlaybackViewModel : ViewModelBase
{
    private readonly SessionWorkflowCoordinator _coordinator;

    [ObservableProperty]
    private ObservableCollection<WorkflowSegmentState> _segments = new();

    [ObservableProperty]
    private WorkflowSegmentState? _selectedSegment;

    [ObservableProperty]
    private bool _isSourcePlaying;

    [ObservableProperty]
    private bool _hasSegments;

    [ObservableProperty]
    private string _statusText = "No segments loaded.";

    [ObservableProperty]
    private bool _isBusy;

    public EmbeddedPlaybackViewModel(SessionWorkflowCoordinator coordinator)
    {
        _coordinator = coordinator;
        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
    }

    public SessionWorkflowCoordinator Coordinator => _coordinator;

    public PlaybackState PlaybackState => _coordinator.PlaybackState;

    public string? ActiveTtsSegmentId => _coordinator.ActiveTtsSegmentId;

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionWorkflowCoordinator.PlaybackState):
                OnPropertyChanged(nameof(PlaybackState));
                break;
            case nameof(SessionWorkflowCoordinator.ActiveTtsSegmentId):
                OnPropertyChanged(nameof(ActiveTtsSegmentId));
                break;
        }
    }

    [RelayCommand]
    private async Task LoadSegmentsAsync()
    {
        try
        {
            var list = await _coordinator.GetSegmentWorkflowListAsync();
            Segments = new ObservableCollection<WorkflowSegmentState>(list);
            HasSegments = Segments.Count > 0;
            StatusText = HasSegments
                ? $"{Segments.Count} segments loaded."
                : "No segments available. Run the workflow first.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load segments: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PlaySourceAtSegmentAsync(WorkflowSegmentState? segment)
    {
        if (segment is null) return;
        try
        {
            IsSourcePlaying = true;
            StatusText = $"Playing source at {segment.StartSeconds:F1}s…";
            await _coordinator.PlaySourceMediaAtSegmentAsync(segment.SegmentId);
        }
        catch (Exception ex)
        {
            StatusText = $"Source playback failed: {ex.Message}";
            IsSourcePlaying = false;
        }
    }

    [RelayCommand]
    private async Task PlayDubbedSegmentAsync(WorkflowSegmentState? segment)
    {
        if (segment is null) return;
        if (!segment.HasTtsAudio)
        {
            StatusText = $"Segment {segment.SegmentId} has no TTS audio.";
            return;
        }
        try
        {
            StatusText = $"Playing dubbed segment at {segment.StartSeconds:F1}s…";
            await _coordinator.PlaySegmentTtsAsync(segment.SegmentId);
        }
        catch (Exception ex)
        {
            StatusText = $"Dubbed playback failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PlayAllDubbedAsync()
    {
        try
        {
            StatusText = "Playing all dubbed segments…";
            await _coordinator.PlayAllDubbedSegmentsAsync();
            StatusText = "Sequence playback finished.";
        }
        catch (Exception ex)
        {
            StatusText = $"Sequence playback failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void StopPlayback()
    {
        _coordinator.StopPlayback();
        _coordinator.StopSourceMedia();
        IsSourcePlaying = false;
        StatusText = "Playback stopped.";
    }

    [RelayCommand]
    private async Task RegenerateTranslationAsync(WorkflowSegmentState? segment)
    {
        if (segment is null) return;
        try
        {
            IsBusy = true;
            StatusText = $"Regenerating translation for {segment.SegmentId}…";
            await _coordinator.RegenerateSegmentTranslationAsync(segment.SegmentId);
            await LoadSegmentsAsync();
            StatusText = $"Translation regenerated for {segment.SegmentId}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Regeneration failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
