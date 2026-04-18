using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using Babel.Player.Models;
using Babel.Player.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels;

public sealed partial class EmbeddedPlaybackPreviewViewModel : ViewModelBase, IDisposable
{
    private enum DubPreviewAudioMode
    {
        DuckSource,
        SeparatedAmbiance,
    }

    private static readonly string DebugLogPath = ResolveDebugLogPath();
    private readonly EmbeddedPlaybackViewModel _parent;
    private readonly SessionWorkflowCoordinator _coordinator;
    private string? _lastKnownSourceMediaPath;
    private string? _loadedAmbiancePath;
    private DubPreviewAudioMode? _lastReportedDubPreviewMode;
    private string? _lastReportedAmbiancePath;
    private bool _isUpdatingPositionFromTimer;
    private bool _isUpdatingActiveSegment;
    private WorkflowSegmentState? _lastDubbedSegment;
    private WorkflowSegmentState[] _sortedSegments = [];
    private double _preMuteVolume = 1.0;
    private bool _isDucked;
    private bool _preFullscreenSegmentPaneVisible = true;
    private string? _activeSrtPath;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _controlsHideTimer;
    private const int ControlsHideDelayMs = 3000;
    private const double AmbianceSyncThresholdMs = 50.0;
    private const double PositionUpdateThresholdMs = 0.5;

    public EmbeddedPlaybackPreviewViewModel(
        EmbeddedPlaybackViewModel parent,
        SessionWorkflowCoordinator coordinator)
    {
        _parent = parent;
        _coordinator = coordinator;
        _lastKnownSourceMediaPath = coordinator.CurrentSession.SourceMediaPath;
        _isSourceMediaLoaded = !string.IsNullOrEmpty(coordinator.CurrentSession.IngestedMediaPath);
        _speechRate = coordinator.TtsPlaybackRate;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += OnPositionTimerTick;
        _positionTimer.Start();

        _controlsHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ControlsHideDelayMs) };
        _controlsHideTimer.Tick += OnControlsHideTimerTick;

        _isBilingualSubtitlesOn = coordinator.CurrentSettings.BilingualSubtitlesEnabled;
    }

    [ObservableProperty]
    private ObservableCollection<WorkflowSegmentState> _segments = new();

    [ObservableProperty]
    private WorkflowSegmentState? _selectedSegment;

    [ObservableProperty]
    private bool _hasSegments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseSourceLabel))]
    private bool _isSourcePaused = true;

    [ObservableProperty]
    private bool _isSourceMediaLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourcePositionFormatted))]
    private double _sourcePositionMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceDurationFormatted))]
    private double _sourceDurationMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeIconLabel))]
    private double _sourceVolume = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeIconLabel))]
    private bool _isMuted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPullTabVisible))]
    [NotifyPropertyChangedFor(nameof(IsPanePullTabVisible))]
    private bool _isFullscreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SegmentPaneToggleLabel))]
    [NotifyPropertyChangedFor(nameof(IsPanePullTabVisible))]
    private bool _isSegmentPaneVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPullTabVisible))]
    [NotifyPropertyChangedFor(nameof(IsPanePullTabVisible))]
    private bool _isControlsVisible = true;

    [ObservableProperty]
    private bool _isDubModeOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleToggleLabel))]
    private bool _isSubtitleModeOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BilingualToggleLabel))]
    private bool _isBilingualSubtitlesOn;

    [RelayCommand]
    private void ToggleBilingualSubtitles()
    {
        IsBilingualSubtitlesOn = !IsBilingualSubtitlesOn;
        _coordinator.CurrentSettings.BilingualSubtitlesEnabled = IsBilingualSubtitlesOn;
        _coordinator.NotifySettingsModified();
        if (IsSubtitleModeOn)
            ApplySubtitleState();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeechRateLabel))]
    private double _speechRate = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioDuckingLabel))]
    private double _audioDuckingDb = -15.0;

    public string SegmentPaneToggleLabel => IsSegmentPaneVisible ? "◄" : "►";
    public bool IsPullTabVisible => !IsFullscreen || IsControlsVisible;
    public bool IsPanePullTabVisible => !IsSegmentPaneVisible && IsPullTabVisible;
    public string PlayPauseSourceLabel => IsSourcePaused ? "▶" : "||";
    public string VolumeIconLabel => IsMuted || SourceVolume == 0
        ? "🔇"
        : SourceVolume < 0.10
            ? "🔈"
            : SourceVolume < 0.51
                ? "🔉"
                : "🔊";
    [ObservableProperty]
    private bool _isCompactVideoChrome;

    /// <summary>Inverse of <see cref="IsCompactVideoChrome"/> for toolbar visibility bindings.</summary>
    public bool IsWideVideoChrome => !IsCompactVideoChrome;

    public string DubModeLabel => IsCompactVideoChrome ? "🎙" : "🎙 Dub";

    partial void OnIsCompactVideoChromeChanged(bool value)
    {
        OnPropertyChanged(nameof(DubModeLabel));
        OnPropertyChanged(nameof(IsWideVideoChrome));
    }
    public string SubtitleToggleLabel => IsSubtitleModeOn ? "CC ✓" : "CC";
    public string BilingualToggleLabel => IsBilingualSubtitlesOn ? "Bi-lang ✓" : "Bi-lang";

    /// <summary>Applies coordinator bilingual subtitle preference after Settings save or other coordinator updates.</summary>
    public void SyncBilingualSubtitlesFromSettings()
    {
        var enabled = _coordinator.CurrentSettings.BilingualSubtitlesEnabled;
        if (IsBilingualSubtitlesOn == enabled)
            return;

        IsBilingualSubtitlesOn = enabled;
        if (IsSubtitleModeOn)
            ApplySubtitleState();
    }

    public void SyncDubMixControlFromSettings()
    {
        OnPropertyChanged(nameof(UsesAmbianceMixControl));
        OnPropertyChanged(nameof(DubMixControlLabel));
        OnPropertyChanged(nameof(DubMixControlTooltip));
        OnPropertyChanged(nameof(DubMixControlDb));
        OnPropertyChanged(nameof(DubMixControlValueLabel));

        // Reapply volumes so preview immediately reflects either the real ambiance
        // bed or the ducked-source fallback after settings/session changes.
        RecalculateOutputVolumes();
    }

    public string SpeechRateLabel => $"{SpeechRate:F1}x";
    public string AudioDuckingLabel => $"{AudioDuckingDb:F1} dB";
    public bool UsesAmbianceMixControl => TryGetAmbiancePreviewPath() is not null;
    public string DubMixControlLabel => UsesAmbianceMixControl ? "Ambience" : "Duck";
    public string DubMixControlTooltip => UsesAmbianceMixControl
        ? "Set separated ambience level under dub preview"
        : "Approximate dub preview by lowering source audio";
    public string DubMixControlValueLabel => $"{DubMixControlDb:F1} dB";
    public double DubMixControlDb
    {
        get => UsesAmbianceMixControl ? _coordinator.CurrentSettings.AmbianceMixDb : AudioDuckingDb;
        set
        {
            if (UsesAmbianceMixControl)
            {
                if (Math.Abs(_coordinator.CurrentSettings.AmbianceMixDb - value) < 0.001)
                    return;

                _coordinator.CurrentSettings.AmbianceMixDb = value;
                _coordinator.NotifySettingsModified();
                SyncDubMixControlFromSettings();
                return;
            }

            if (Math.Abs(AudioDuckingDb - value) < 0.001)
                return;

            AudioDuckingDb = value;
        }
    }
    public string SourcePositionFormatted => FormatMs(SourcePositionMs);
    public string SourceDurationFormatted => FormatMs(SourceDurationMs);

    public async Task HandleCurrentSessionChangedAsync()
    {
        var oldPath = _lastKnownSourceMediaPath;
        var newPath = _coordinator.CurrentSession.SourceMediaPath;
        IsSourceMediaLoaded = !string.IsNullOrEmpty(_coordinator.CurrentSession.IngestedMediaPath);

        PauseAmbiancePreview(resetLoadedPath: true);
        _lastReportedDubPreviewMode = null;
        _lastReportedAmbiancePath = null;
        SyncDubMixControlFromSettings();

        if (newPath != oldPath)
        {
            _lastKnownSourceMediaPath = newPath;
            IsSourcePaused = true;
            _lastDubbedSegment = null;
            _isUpdatingActiveSegment = true;
            SelectedSegment = null;
            _isUpdatingActiveSegment = false;
        }

        if (_coordinator.CurrentSession.Stage >= SessionWorkflowStage.Transcribed)
        {
            await RefreshSegmentsAsync();
        }
        else
        {
            ClearSegments();
        }
    }

    public void ClearSegments()
    {
        Segments = new ObservableCollection<WorkflowSegmentState>();
        HasSegments = false;
        _isUpdatingActiveSegment = true;
        try
        {
            SelectedSegment = null;
        }
        finally
        {
            _isUpdatingActiveSegment = false;
        }
    }

    public void ResetInteractiveModes()
    {
        if (IsSubtitleModeOn)
            IsSubtitleModeOn = false;

        if (IsDubModeOn)
            IsDubModeOn = false;
    }

    public void NotifyControlsActivity()
    {
        IsControlsVisible = true;
        _controlsHideTimer.Stop();
        if (!IsSourcePaused && IsFullscreen)
            _controlsHideTimer.Start();
    }

    public void ReapplySubtitlesIfActive()
    {
        if (IsSubtitleModeOn && _activeSrtPath is not null)
            ApplySubtitleState();
    }

    /// <summary>
    /// Selects a segment in the preview list and seeks source media. Used from the speaker setup wizard while the main window stays interactive.
    /// </summary>
    public async Task SelectSegmentAndSeekAsync(WorkflowSegmentState segment, bool playSource = false)
    {
        ArgumentNullException.ThrowIfNull(segment);

        _isUpdatingActiveSegment = true;
        try
        {
            SelectedSegment = segment;
        }
        finally
        {
            _isUpdatingActiveSegment = false;
        }

        _parent.SpeakerRouting.TrySelectSpeakerForSegment(segment.SpeakerId);

        try
        {
            var player = _coordinator.SourceMediaPlayer;
            if (player is null)
            {
                var ingestedPath = _coordinator.CurrentSession.IngestedMediaPath;
                if (string.IsNullOrEmpty(ingestedPath))
                    return;

                player = _coordinator.GetOrCreateSourcePlayer();
                player.Load(ingestedPath);
            }

            player.Seek((long)(segment.StartSeconds * 1000));

            if (!playSource)
            {
                player.Pause();
                IsSourcePaused = true;
                if (IsDubModeOn)
                    ApplyDubForSegment(null);
            }
            else
            {
                await Task.Run(player.Play);
                IsSourcePaused = false;
                _parent.ClearStatusErrorDetail();
                if (IsDubModeOn && !IsSourcePaused)
                    ApplyDubForSegment(segment);
            }
        }
        catch (Exception ex)
        {
            _parent.StatusText = $"Seek failed: {ex.Message}";
            _parent.SetStatusErrorDetail("Source seek failed", ex);
        }
    }

    [RelayCommand]
    public async Task RefreshSegmentsAsync(System.Collections.Generic.List<WorkflowSegmentState>? segments = null)
    {
        try
        {
            var refreshStopwatch = Stopwatch.StartNew();
            var list = segments ?? await _coordinator.GetSegmentWorkflowListAsync();
            _isUpdatingActiveSegment = true;
            try
            {
                SelectedSegment = null;
                Segments = new ObservableCollection<WorkflowSegmentState>(list);
                HasSegments = Segments.Count > 0;
                _parent.StatusText = HasSegments
                    ? $"{Segments.Count} segments loaded."
                    : "No segments available. Run the workflow first.";
                _parent.ClearStatusErrorDetail();
                if (IsSubtitleModeOn)
                {
                    ApplySubtitleState();
                }
            }
            finally
            {
                _isUpdatingActiveSegment = false;
            }
            refreshStopwatch.Stop();
        }
        catch (Exception ex)
        {
            _parent.StatusText = $"Failed to load segments: {ex.Message}";
            _parent.SetStatusErrorDetail("Load Segments failed", ex);
        }
    }

    [RelayCommand]
    private async Task PlayPauseSourceAsync()
    {
        var player = _coordinator.SourceMediaPlayer;
        if (player is null)
        {
            var ingestedPath = _coordinator.CurrentSession.IngestedMediaPath;
            if (string.IsNullOrEmpty(ingestedPath))
                return;

            player = _coordinator.GetOrCreateSourcePlayer();
            player.Load(ingestedPath);
        }

        if (IsSourcePaused)
        {
            try
            {
                await Task.Run(player.Play);
                IsSourcePaused = false;
                if (IsDubModeOn)
                    SyncDubToCurrentPosition(seekVideoToSegmentStart: true);
                _parent.ClearStatusErrorDetail();
            }
            catch (Exception ex)
            {
                _parent.StatusText = $"Play failed: {ex.Message}";
                _parent.SetStatusErrorDetail("Source Playback failed", ex);
            }
        }
        else
        {
            player.Pause();
            IsSourcePaused = true;
            if (IsDubModeOn)
                ApplyDubForSegment(null);
        }
    }

    [RelayCommand]
    private void ToggleDubMode() => IsDubModeOn = !IsDubModeOn;

    [RelayCommand]
    private void SkipBackward()
    {
        if (!HasSegments)
            return;

        var previous = FindPreviousSegmentEndingBefore((SourcePositionMs / 1000.0) - 0.1);
        if (previous is not null)
            _ = SeekAndPlayAsync(previous);
    }

    [RelayCommand]
    private void SkipForward()
    {
        if (!HasSegments)
            return;

        var next = FindNextSegmentStartingAfter((SourcePositionMs / 1000.0) + 0.1);
        if (next is not null)
            _ = SeekAndPlayAsync(next);
    }

    [RelayCommand]
    private void Rewind()
    {
        if (_coordinator.SourceMediaPlayer is null)
            return;

        SourcePositionMs = Math.Max(0, SourcePositionMs - 10_000);
    }

    [RelayCommand]
    private void FastForward()
    {
        if (_coordinator.SourceMediaPlayer is null)
            return;

        SourcePositionMs = SourceDurationMs > 0
            ? Math.Min(SourceDurationMs, SourcePositionMs + 10_000)
            : SourcePositionMs + 10_000;
    }

    [RelayCommand]
    private void ToggleMute()
    {
        if (IsMuted)
        {
            IsMuted = false;
            SourceVolume = _preMuteVolume > 0 ? _preMuteVolume : 1.0;
        }
        else
        {
            _preMuteVolume = SourceVolume;
            IsMuted = true;
            SourceVolume = 0;
        }
    }

    [RelayCommand]
    private void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    [RelayCommand]
    private void ToggleSegmentPane() => IsSegmentPaneVisible = !IsSegmentPaneVisible;

    [RelayCommand]
    private void ToggleSubtitles()
    {
        if (!HasSegments)
            return;

        IsSubtitleModeOn = !IsSubtitleModeOn;
    }

    public void Dispose()
    {
        PauseAmbiancePreview(resetLoadedPath: true);
        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTimerTick;
        _controlsHideTimer.Stop();
        _controlsHideTimer.Tick -= OnControlsHideTimerTick;
        DeleteActiveSubtitleFile();
    }

    partial void OnSourceVolumeChanged(double value)
    {
        if (IsMuted && value > 0)
            IsMuted = false;

        RecalculateOutputVolumes();
    }

    partial void OnAudioDuckingDbChanged(double value)
    {
        RecalculateOutputVolumes();
        OnPropertyChanged(nameof(DubMixControlDb));
        OnPropertyChanged(nameof(DubMixControlValueLabel));
    }

    partial void OnIsMutedChanged(bool value) => RecalculateOutputVolumes();

    partial void OnSpeechRateChanged(double value)
    {
        _coordinator.TtsPlaybackRate = value;
    }

    partial void OnSourcePositionMsChanged(double value)
    {
        if (_isUpdatingPositionFromTimer)
            return;

        _coordinator.SourceMediaPlayer?.Seek((long)value);
        if (IsDubModeOn && ResolveDubPreviewAudioMode() == DubPreviewAudioMode.SeparatedAmbiance)
            SyncSeparatedAmbiancePreview(shouldPlay: !IsSourcePaused, forceSeek: true);
        if (IsDubModeOn && !IsSourcePaused)
            SyncDubToCurrentPosition(seekVideoToSegmentStart: true);
    }

    partial void OnSelectedSegmentChanged(WorkflowSegmentState? value)
    {
        _parent.SpeakerRouting.TrySelectSpeakerForSegment(value?.SpeakerId);

        if (_isUpdatingActiveSegment || value is null || !IsSourceMediaLoaded)
            return;

        _ = SeekAndPlayAsync(value);
    }

    partial void OnIsDubModeOnChanged(bool value)
    {
        if (!value)
        {
            _lastReportedDubPreviewMode = null;
            _lastReportedAmbiancePath = null;
            PauseAmbiancePreview();
            ApplyDubForSegment(null);
        }
        else if (!IsSourcePaused)
        {
            SyncDubToCurrentPosition(seekVideoToSegmentStart: true);
        }
    }

    partial void OnIsFullscreenChanged(bool value)
    {
        if (value)
        {
            _preFullscreenSegmentPaneVisible = IsSegmentPaneVisible;
            IsSegmentPaneVisible = false;
            NotifyControlsActivity();
        }
        else
        {
            IsSegmentPaneVisible = _preFullscreenSegmentPaneVisible;
            _controlsHideTimer.Stop();
            IsControlsVisible = true;
        }
    }

    partial void OnIsSubtitleModeOnChanged(bool value) => ApplySubtitleState();

    partial void OnIsSourcePausedChanged(bool value)
    {
        if (value)
        {
            _controlsHideTimer.Stop();
            IsControlsVisible = true;
            PauseAmbiancePreview();
        }
        else
        {
            NotifyControlsActivity();
            if (IsDubModeOn && ResolveDubPreviewAudioMode() == DubPreviewAudioMode.SeparatedAmbiance)
                SyncSeparatedAmbiancePreview(shouldPlay: true, forceSeek: true);
        }
    }

    partial void OnSegmentsChanged(ObservableCollection<WorkflowSegmentState> value)
    {
        var sorted = value.ToArray();
        Array.Sort(sorted, (a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        _sortedSegments = sorted;
        _parent.SpeakerRouting.RebuildSpeakerIds(value, SelectedSegment?.SpeakerId);
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        var player = _coordinator.SourceMediaPlayer;
        if (player is null || player.Duration == 0)
            return;

        _isUpdatingPositionFromTimer = true;
        var newDurationMs = player.Duration;
        if (Math.Abs(SourceDurationMs - newDurationMs) > PositionUpdateThresholdMs)
            SourceDurationMs = newDurationMs;

        var newPositionMs = player.CurrentTime;
        if (Math.Abs(SourcePositionMs - newPositionMs) > PositionUpdateThresholdMs)
            SourcePositionMs = newPositionMs;
        _isUpdatingPositionFromTimer = false;

        UpdateActiveSegment();
        if (IsDubModeOn)
            UpdateDubMode();
    }

    private void UpdateActiveSegment()
    {
        var currentSegment = FindSegmentAt(SourcePositionMs / 1000.0);
        if (currentSegment?.SegmentId == SelectedSegment?.SegmentId)
            return;

        _isUpdatingActiveSegment = true;
        SelectedSegment = currentSegment;
        _isUpdatingActiveSegment = false;
    }

    public Task PreviewSelectedSegmentWithPauseAsync()
    {
        if (SelectedSegment is null || !SelectedSegment.HasTtsAudio || !IsSourceMediaLoaded)
            return Task.CompletedTask;

        return SeekAndPlayAsync(SelectedSegment, SegmentTimingMode.Pause);
    }

    private Task SeekAndPlayAsync(WorkflowSegmentState segment) =>
        SeekAndPlayAsync(segment, null);

    private async Task SeekAndPlayAsync(WorkflowSegmentState segment, SegmentTimingMode? previewTimingOverride)
    {
        var player = _coordinator.SourceMediaPlayer;
        if (player is null)
        {
            await PlaySourceAtSegmentAsync(segment);
            if ((previewTimingOverride.HasValue || IsDubModeOn) && !IsSourcePaused)
                ApplyDubForSegment(segment, previewTimingOverride: previewTimingOverride);
            return;
        }

        player.Seek((long)(segment.StartSeconds * 1000));

        if (IsSourcePaused || player.HasEnded)
        {
            try
            {
                await Task.Run(player.Play);
                IsSourcePaused = false;
                _parent.ClearStatusErrorDetail();
            }
            catch (Exception ex)
            {
                _parent.StatusText = $"Play failed: {ex.Message}";
                _parent.SetStatusErrorDetail("Source Playback failed", ex);
                return;
            }
        }

        if ((previewTimingOverride.HasValue || IsDubModeOn) && !IsSourcePaused)
            ApplyDubForSegment(segment, previewTimingOverride: previewTimingOverride);
    }

    private async Task PlaySourceAtSegmentAsync(WorkflowSegmentState? segment)
    {
        if (segment is null)
            return;

        try
        {
            _parent.StatusText = $"Playing source at {segment.StartSeconds:F1}s…";
            await _coordinator.PlaySourceMediaAtSegmentAsync(segment.SegmentId);
            IsSourcePaused = false;
            _parent.ClearStatusErrorDetail();
        }
        catch (Exception ex)
        {
            _parent.StatusText = $"Source playback failed: {ex.Message}";
            _parent.SetStatusErrorDetail("Source Playback failed", ex);
        }
    }

    private void RecalculateOutputVolumes()
    {
        var masterGain = IsMuted ? 0.0 : SourceVolume;
        var previewMode = ResolveDubPreviewAudioMode();
        var duckingDb = previewMode == DubPreviewAudioMode.SeparatedAmbiance
            ? _coordinator.CurrentSettings.AmbianceMixDb
            : AudioDuckingDb;
        var sourceGain = IsDubModeOn && previewMode == DubPreviewAudioMode.SeparatedAmbiance
            ? 0.0
            : _isDucked
                ? masterGain * Math.Pow(10.0, duckingDb / 20.0)
                : masterGain;
        var ambianceGain = IsDubModeOn && previewMode == DubPreviewAudioMode.SeparatedAmbiance
            ? masterGain * Math.Pow(10.0, _coordinator.CurrentSettings.AmbianceMixDb / 20.0)
            : 0.0;

        _coordinator.SourceMediaPlayer?.Volume = sourceGain;
        if (_coordinator.AmbiancePlayer is { } ambiancePlayer)
            ambiancePlayer.Volume = ambianceGain;
        _coordinator.TtsVolume = masterGain;
    }

    private void ApplyDucking()
    {
        if (_isDucked)
            return;

        _isDucked = true;
        RecalculateOutputVolumes();
    }

    private void RestoreDucking()
    {
        if (!_isDucked)
            return;

        _isDucked = false;
        RecalculateOutputVolumes();
    }

    private WorkflowSegmentState? FindSegmentAt(double positionSeconds)
    {
        if (_sortedSegments.Length == 0)
            return null;

        var low = 0;
        var high = _sortedSegments.Length - 1;
        var candidate = -1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            if (_sortedSegments[middle].StartSeconds <= positionSeconds)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (candidate < 0)
            return null;

        var segment = _sortedSegments[candidate];
        return positionSeconds < segment.EndSeconds ? segment : null;
    }

    private WorkflowSegmentState? FindPreviousSegmentEndingBefore(double positionSeconds)
    {
        if (_sortedSegments.Length == 0)
            return null;

        var low = 0;
        var high = _sortedSegments.Length - 1;
        var candidate = -1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            if (_sortedSegments[middle].EndSeconds <= positionSeconds)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return candidate >= 0 ? _sortedSegments[candidate] : null;
    }

    private WorkflowSegmentState? FindNextSegmentStartingAfter(double positionSeconds)
    {
        if (_sortedSegments.Length == 0)
            return null;

        var low = 0;
        var high = _sortedSegments.Length - 1;
        var candidate = -1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            if (_sortedSegments[middle].StartSeconds > positionSeconds)
            {
                candidate = middle;
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }

        return candidate >= 0 ? _sortedSegments[candidate] : null;
    }

    private void ApplyDubForSegment(
        WorkflowSegmentState? segment,
        bool seekVideoToSegmentStart = false,
        SegmentTimingMode? previewTimingOverride = null)
    {
        RestoreDucking();
        _coordinator.StopTtsPlayback();
        _lastDubbedSegment = segment;
        var previewMode = ResolveDubPreviewAudioMode();

        if (!IsDubModeOn)
        {
            PauseAmbiancePreview();
            if (segment is null)
                return;
        }

        if (seekVideoToSegmentStart && segment is not null)
            _coordinator.SourceMediaPlayer?.Seek((long)(segment.StartSeconds * 1000));

        if (IsDubModeOn && previewMode == DubPreviewAudioMode.SeparatedAmbiance)
        {
            SyncSeparatedAmbiancePreview(shouldPlay: !IsSourcePaused, forceSeek: seekVideoToSegmentStart);
        }
        else
        {
            PauseAmbiancePreview();
            if (IsDubModeOn)
                AnnounceDubPreviewMode(DubPreviewAudioMode.DuckSource, null);
        }

        RecalculateOutputVolumes();

        if (segment is null)
            return;

        if (segment.HasTtsAudio)
        {
            // Resolve effective timing mode: per-segment override takes priority, then session setting.
            var effectiveMode = previewTimingOverride
                ?? segment.TimingModeOverride
                ?? _coordinator.CurrentSettings.DubTimingMode;
            if (previewMode == DubPreviewAudioMode.DuckSource)
                ApplyDucking();
            _ = _coordinator.PlayTtsForSegmentAsync(segment.SegmentId, segment, effectiveMode);
        }
    }

    private void UpdateDubMode()
    {
        if (IsSourcePaused)
            return;

        var currentSegment = FindSegmentAt(SourcePositionMs / 1000.0);
        if (currentSegment?.SegmentId == _lastDubbedSegment?.SegmentId)
            return;

        ApplyDubForSegment(currentSegment);
    }

    private void SyncDubToCurrentPosition(bool seekVideoToSegmentStart) =>
        ApplyDubForSegment(FindSegmentAt(SourcePositionMs / 1000.0), seekVideoToSegmentStart);

    private string? TryGetAmbiancePreviewPath()
    {
        var path = _coordinator.CurrentSession.AmbianceAudioPath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? path
            : null;
    }

    private DubPreviewAudioMode ResolveDubPreviewAudioMode() =>
        TryGetAmbiancePreviewPath() is not null
            ? DubPreviewAudioMode.SeparatedAmbiance
            : DubPreviewAudioMode.DuckSource;

    private void AnnounceDubPreviewMode(DubPreviewAudioMode mode, string? ambiancePath)
    {
        if (!IsDubModeOn)
            return;

        if (_lastReportedDubPreviewMode == mode &&
            string.Equals(_lastReportedAmbiancePath, ambiancePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastReportedDubPreviewMode = mode;
        _lastReportedAmbiancePath = ambiancePath;

        if (mode == DubPreviewAudioMode.SeparatedAmbiance)
        {
            _coordinator.Log.Debug(
                $"Dub preview mode: real-ambiance-preview, path='{ambiancePath}', gainDb={_coordinator.CurrentSettings.AmbianceMixDb:F1}");
            _parent.StatusText = "Dub preview uses separated ambience.";
            return;
        }

        _coordinator.Log.Debug(
            $"Dub preview mode: ducked-source-fallback, gainDb={AudioDuckingDb:F1}");
        _parent.StatusText = "Dub preview uses source audio fallback.";
    }

    private void PauseAmbiancePreview(bool resetLoadedPath = false)
    {
        _coordinator.AmbiancePlayer?.Pause();
        if (resetLoadedPath)
            _loadedAmbiancePath = null;
    }

    private void SyncSeparatedAmbiancePreview(bool shouldPlay, bool forceSeek = false)
    {
        var sourcePlayer = _coordinator.SourceMediaPlayer;
        var ambiancePath = TryGetAmbiancePreviewPath();
        if (sourcePlayer is null || ambiancePath is null)
        {
            PauseAmbiancePreview(resetLoadedPath: ambiancePath is null);
            return;
        }

        var ambiancePlayer = _coordinator.GetOrCreateAmbiancePlayer();
        if (!string.Equals(_loadedAmbiancePath, ambiancePath, StringComparison.OrdinalIgnoreCase))
        {
            ambiancePlayer.Load(ambiancePath);
            _loadedAmbiancePath = ambiancePath;
            forceSeek = true;
        }

        ambiancePlayer.PlaybackRate = sourcePlayer.PlaybackRate;
        ambiancePlayer.Volume = (IsMuted ? 0.0 : SourceVolume) *
            Math.Pow(10.0, _coordinator.CurrentSettings.AmbianceMixDb / 20.0);

        var sourcePositionMs = sourcePlayer.CurrentTime;
        if (forceSeek || Math.Abs(ambiancePlayer.CurrentTime - sourcePositionMs) > AmbianceSyncThresholdMs)
            ambiancePlayer.Seek(sourcePositionMs);

        AnnounceDubPreviewMode(DubPreviewAudioMode.SeparatedAmbiance, ambiancePath);

        if (shouldPlay)
            ambiancePlayer.Play();
        else
            ambiancePlayer.Pause();
    }

    /// <summary>
    /// Applies a per-segment timing mode override through the coordinator (session state owner),
    /// then mirrors the result into the preview list and sorted cache.
    /// </summary>
    public void ApplySegmentTimingOverride(string segmentId, SegmentTimingMode? mode)
    {
        _coordinator.SetSegmentTimingOverride(segmentId, mode);

        for (int i = 0; i < Segments.Count; i++)
        {
            if (Segments[i].SegmentId == segmentId)
            {
                Segments[i] = Segments[i] with { TimingModeOverride = mode };
                // Rebuild sorted cache so playback lookup picks up the change.
                _sortedSegments = [.. Segments.OrderBy(s => s.StartSeconds)];
                // Refresh the SelectedSegment reference so the inspection VM re-reads it.
                if (SelectedSegment?.SegmentId == segmentId)
                    SelectedSegment = Segments[i];
                return;
            }
        }
    }

    private void OnControlsHideTimerTick(object? sender, EventArgs e)
    {
        _controlsHideTimer.Stop();
        IsControlsVisible = false;
    }

    private void DeleteActiveSubtitleFile()
    {
        if (string.IsNullOrEmpty(_activeSrtPath))
            return;

        try
        {
            if (File.Exists(_activeSrtPath))
                File.Delete(_activeSrtPath);
        }
        catch
        {
            // Best-effort cleanup only.
        }
        finally
        {
            _activeSrtPath = null;
        }
    }

    private void ApplySubtitleState()
    {
        if (_coordinator.SourceMediaPlayer is not LibMpvEmbeddedTransport player)
            return;

        if (IsSubtitleModeOn)
        {
            var srt = SrtGenerator.Generate(Segments, IsBilingualSubtitlesOn);
            DeleteActiveSubtitleFile();
            _activeSrtPath = Path.Combine(Path.GetTempPath(), $"subs_{Guid.NewGuid():N}.srt");
            File.WriteAllText(_activeSrtPath, srt, Encoding.UTF8);
            player.RemoveAllSubtitleTracks();
            player.LoadSubtitleTrack(_activeSrtPath);
            player.SubtitlesVisible = true;
        }
        else
        {
            player.SubtitlesVisible = false;
            DeleteActiveSubtitleFile();
        }
    }

    private static string FormatMs(double milliseconds) =>
        milliseconds <= 0 ? "0:00" : TimeSpan.FromMilliseconds(milliseconds).ToString(@"m\:ss");

    private static void WriteDebugLog(string runId, string hypothesisId, string location, string message, object data)
    {
        var payload = new
        {
            sessionId = "f76224",
            runId,
            hypothesisId,
            location,
            message,
            data,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        try
        {
            var line = JsonSerializer.Serialize(payload);
            File.AppendAllText(DebugLogPath, line + Environment.NewLine);
        }
        catch
        {
            // Swallow debug log failures.
        }
    }

    private static string ResolveDebugLogPath()
    {
        var envPath = Environment.GetEnvironmentVariable("BABEL_DEBUG_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return envPath;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Babel-Player.sln")))
                return Path.Combine(dir.FullName, "debug-f76224.log");
            dir = dir.Parent;
        }

        return Path.Combine(Environment.CurrentDirectory, "debug-f76224.log");
    }
}
