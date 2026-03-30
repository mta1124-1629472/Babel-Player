using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Babel.Deck.Models;
using Babel.Deck.Services;

namespace Babel.Deck.ViewModels;

public partial class EmbeddedPlaybackViewModel : ViewModelBase
{
    private readonly SessionWorkflowCoordinator _coordinator;
    private string? _lastKnownSourceMediaPath;
    private bool _isUpdatingPositionFromTimer;
    private WorkflowSegmentState? _lastDubbedSegment;

    [ObservableProperty]
    private ObservableCollection<WorkflowSegmentState> _segments = [];

    [ObservableProperty]
    private WorkflowSegmentState? _selectedSegment;

    [ObservableProperty]
    private bool _hasSegments;

    [ObservableProperty]
    private string _statusText = "No segments loaded.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isSourceMediaLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseSourceLabel))]
    private bool _isSourcePaused = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourcePositionFormatted))]
    private double _sourcePositionMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceDurationFormatted))]
    private double _sourceDurationMs;

    [ObservableProperty]
    private double _sourceVolume = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DubModeLabel))]
    private bool _isDubModeOn;

    public EmbeddedPlaybackViewModel(SessionWorkflowCoordinator coordinator)
    {
        _coordinator = coordinator;
        _lastKnownSourceMediaPath = coordinator.CurrentSession.SourceMediaPath;
        _isSourceMediaLoaded = !string.IsNullOrEmpty(coordinator.CurrentSession.IngestedMediaPath);
        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += OnPositionTimerTick;
        timer.Start();
    }

    public SessionWorkflowCoordinator Coordinator => _coordinator;

    public PlaybackState PlaybackState => _coordinator.PlaybackState;

    public string? ActiveTtsSegmentId => _coordinator.ActiveTtsSegmentId;

    public string PlayPauseSourceLabel => IsSourcePaused ? "▶ Play" : "⏸ Pause";

    public string DubModeLabel => IsDubModeOn ? "🎙 Dub: On" : "🎙 Dub: Off";

    public string SourcePositionFormatted => FormatMs(SourcePositionMs);
    public string SourceDurationFormatted => FormatMs(SourceDurationMs);

    private static string FormatMs(double ms) =>
        ms <= 0 ? "0:00" : TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss");

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        var player = _coordinator.SourceMediaPlayer;
        if (player == null || player.Duration == 0) return;

        _isUpdatingPositionFromTimer = true;
        SourceDurationMs = player.Duration;
        SourcePositionMs = player.CurrentTime;
        _isUpdatingPositionFromTimer = false;

        if (IsDubModeOn) UpdateDubMode();
    }

    partial void OnSourcePositionMsChanged(double value)
    {
        if (_isUpdatingPositionFromTimer) return;
        _coordinator.SourceMediaPlayer?.Seek((long)value);
        if (IsDubModeOn)
        {
            _coordinator.StopTtsPlayback();
            _lastDubbedSegment = null;
        }
    }

    partial void OnSourceVolumeChanged(double value)
    {
        var player = _coordinator.SourceMediaPlayer;
        if (player != null)
            player.Volume = value;
    }

    partial void OnIsDubModeOnChanged(bool value)
    {
        if (!value)
        {
            _coordinator.StopTtsPlayback();
            _lastDubbedSegment = null;
        }
    }

    private WorkflowSegmentState? FindSegmentAt(double positionSeconds)
        => Segments.FirstOrDefault(s => positionSeconds >= s.StartSeconds && positionSeconds < s.EndSeconds);

    private void UpdateDubMode()
    {
        var currentSeg = FindSegmentAt(SourcePositionMs / 1000.0);
        if (currentSeg?.SegmentId == _lastDubbedSegment?.SegmentId) return;
        _lastDubbedSegment = currentSeg;
        _coordinator.StopTtsPlayback();
        if (currentSeg?.HasTtsAudio == true)
            _ = _coordinator.PlayTtsForSegmentAsync(currentSeg.SegmentId);
    }

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
            case nameof(SessionWorkflowCoordinator.CurrentSession):
                var newPath = _coordinator.CurrentSession.SourceMediaPath;
                IsSourceMediaLoaded = !string.IsNullOrEmpty(_coordinator.CurrentSession.IngestedMediaPath);
                if (newPath != _lastKnownSourceMediaPath)
                {
                    _lastKnownSourceMediaPath = newPath;
                    // Media switched — auto-play triggered by MainWindow code-behind
                    IsSourcePaused = false;
                    _lastDubbedSegment = null;
                    _ = LoadSegmentsAsync();
                }
                break;
        }
    }

    [RelayCommand]
    private async Task PlayPauseSourceAsync()
    {
        var player = _coordinator.SourceMediaPlayer;
        if (player == null)
        {
            var ingestedPath = _coordinator.CurrentSession.IngestedMediaPath;
            if (string.IsNullOrEmpty(ingestedPath)) return;
            player = _coordinator.GetOrCreateSourcePlayer();
            player.Load(ingestedPath);
        }

        if (IsSourcePaused)
        {
            try
            {
                await Task.Run(() => player.Play());
                IsSourcePaused = false;
            }
            catch (Exception ex)
            {
                StatusText = $"Play failed: {ex.Message}";
            }
        }
        else
        {
            player.Pause();
            IsSourcePaused = true;
        }
    }

    [RelayCommand]
    private void ToggleDubMode() => IsDubModeOn = !IsDubModeOn;

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
            StatusText = $"Playing source at {segment.StartSeconds:F1}s…";
            await _coordinator.PlaySourceMediaAtSegmentAsync(segment.SegmentId);
            IsSourcePaused = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Source playback failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void StopPlayback()
    {
        _coordinator.StopPlayback();
        _lastDubbedSegment = null;
        IsSourcePaused = true;
        StatusText = "Playback stopped.";
    }

    [RelayCommand]
    private async Task RunPipelineAsync()
    {
        try
        {
            IsBusy = true;
            var stage = _coordinator.CurrentSession.Stage;
            System.Diagnostics.Debug.WriteLine($"[Pipeline] Starting. Current stage: {stage}, SourceLanguage: {_coordinator.CurrentSession.SourceLanguage ?? "(null)"}");

            if (stage < SessionWorkflowStage.Transcribed
                || _coordinator.CurrentSession.SourceLanguage is null or "unknown")
            {
                StatusText = "Transcribing audio…";
                System.Diagnostics.Debug.WriteLine("[Pipeline] Running transcription…");
                await _coordinator.TranscribeMediaAsync();
                System.Diagnostics.Debug.WriteLine($"[Pipeline] Transcription done. Language: {_coordinator.CurrentSession.SourceLanguage}");
                StatusText = "Transcription complete. Loading segments…";
                await LoadSegmentsAsync();
                System.Diagnostics.Debug.WriteLine($"[Pipeline] Segments loaded after transcription: {Segments.Count}");
                StatusText = $"Transcribed {Segments.Count} segments. Ready for translation.";
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Pipeline] Skipping transcription (already done).");
            }

            stage = _coordinator.CurrentSession.Stage;
            if (stage < SessionWorkflowStage.Translated)
            {
                StatusText = $"Translating ({_coordinator.CurrentSession.SourceLanguage} → en)…";
                System.Diagnostics.Debug.WriteLine("[Pipeline] Running translation…");
                await _coordinator.TranslateTranscriptAsync();
                System.Diagnostics.Debug.WriteLine("[Pipeline] Translation done.");
                StatusText = "Translation complete. Loading segments…";
                await LoadSegmentsAsync();
                System.Diagnostics.Debug.WriteLine($"[Pipeline] Segments loaded after translation: {Segments.Count}");
                StatusText = $"Translated {Segments.Count} segments. Ready for TTS.";
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Pipeline] Skipping translation (already done).");
            }

            stage = _coordinator.CurrentSession.Stage;
            if (stage < SessionWorkflowStage.TtsGenerated)
            {
                StatusText = "Generating dubbed audio…";
                System.Diagnostics.Debug.WriteLine("[Pipeline] Running TTS…");
                await _coordinator.GenerateTtsAsync();
                System.Diagnostics.Debug.WriteLine("[Pipeline] TTS done.");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Pipeline] Skipping TTS (already done).");
            }

            StatusText = "Loading segments…";
            await LoadSegmentsAsync();
            System.Diagnostics.Debug.WriteLine($"[Pipeline] Complete. {Segments.Count} segments loaded.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Pipeline] Failed: {ex}");
            StatusText = $"Pipeline failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
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
