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
    private static readonly string DebugLogPath = ResolveDebugLogPath();
    private readonly EmbeddedPlaybackViewModel _parent;
    private readonly SessionWorkflowCoordinator _coordinator;
    private string? _lastKnownSourceMediaPath;
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
    [NotifyPropertyChangedFor(nameof(SpeechRateLabel))]
    private double _speechRate = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioDuckingLabel))]
    private double _audioDuckingDb = -15.0;

    public string SegmentPaneToggleLabel => IsSegmentPaneVisible ? "\u25C4" : "\u25BA";
    public bool IsPullTabVisible => !IsFullscreen || IsControlsVisible;
    public bool IsPanePullTabVisible => !IsSegmentPaneVisible && IsPullTabVisible;
    public string PlayPauseSourceLabel => IsSourcePaused ? "\u25B6\uFE0E" : "\u23F8\uFE0E";
    public string VolumeIconLabel => IsMuted || SourceVolume == 0
        ? "\U0001F507"
        : SourceVolume < 0.10
            ? "\U0001F508"
            : SourceVolume < 0.51
                ? "\U0001F509"
                : "\U0001F50A";
    public string DubModeLabel => "🎙 Dub";
    public string SubtitleToggleLabel => IsSubtitleModeOn ? "CC ✓" : "CC";
    public string SpeechRateLabel => $"{SpeechRate:F1}x";
    public string AudioDuckingLabel => $"{AudioDuckingDb:F1} dB";
    public string SourcePositionFormatted => FormatMs(SourcePositionMs);
    public string SourceDurationFormatted => FormatMs(SourceDurationMs);

    public async Task HandleCurrentSessionChangedAsync()
    {
        var oldPath = _lastKnownSourceMediaPath;
        var newPath = _coordinator.CurrentSession.SourceMediaPath;
        IsSourceMediaLoaded = !string.IsNullOrEmpty(_coordinator.CurrentSession.IngestedMediaPath);

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

    [RelayCommand]
    public async Task RefreshSegmentsAsync(System.Collections.Generic.List<WorkflowSegmentState>? segments = null)
    {
        try
        {
            var refreshStopwatch = Stopwatch.StartNew();
            // #region agent log
            WriteDebugLog(
                runId: "initial",
                hypothesisId: "H28",
                location: "EmbeddedPlaybackPreviewViewModel.cs:RefreshSegmentsAsync",
                message: "RefreshSegmentsAsync started",
                data: new
                {
                    providedSegments = segments?.Count,
                    managedThreadId = Environment.CurrentManagedThreadId,
                    syncContext = Dispatcher.UIThread.CheckAccess() ? "UI" : "NonUI",
                });
            // #endregion
            var list = segments ?? await _coordinator.GetSegmentWorkflowListAsync();
            // #region agent log
            WriteDebugLog(
                runId: "initial",
                hypothesisId: "H28",
                location: "EmbeddedPlaybackPreviewViewModel.cs:RefreshSegmentsAsync",
                message: "RefreshSegmentsAsync loaded workflow segment list",
                data: new
                {
                    listCount = list.Count,
                });
            // #endregion
            _isUpdatingActiveSegment = true;
            try
            {
                // #region agent log
                WriteDebugLog(
                    runId: "initial",
                    hypothesisId: "H29",
                    location: "EmbeddedPlaybackPreviewViewModel.cs:RefreshSegmentsAsync",
                    message: "About to replace Segments collection",
                    data: new
                    {
                        incomingCount = list.Count,
                    });
                // #endregion
                SelectedSegment = null;
                Segments = new ObservableCollection<WorkflowSegmentState>(list);
                // #region agent log
                WriteDebugLog(
                    runId: "initial",
                    hypothesisId: "H29",
                    location: "EmbeddedPlaybackPreviewViewModel.cs:RefreshSegmentsAsync",
                    message: "Replaced Segments collection",
                    data: new
                    {
                        segmentsCount = Segments.Count,
                    });
                // #endregion
                HasSegments = Segments.Count > 0;
                _parent.StatusText = HasSegments
                    ? $"{Segments.Count} segments loaded."
                    : "No segments available. Run the workflow first.";
                _parent.ClearStatusErrorDetail();
                if (IsSubtitleModeOn)
                {
                    // #region agent log
                    WriteDebugLog(
                        runId: "initial",
                        hypothesisId: "H29",
                        location: "EmbeddedPlaybackPreviewViewModel.cs:RefreshSegmentsAsync",
                        message: "Applying subtitle state during refresh",
                        data: new
                        {
                            isSubtitleModeOn = IsSubtitleModeOn,
                            activeSrtPath = _activeSrtPath,
                        });
                    // #endregion
                    ApplySubtitleState();
                    // #region agent log
                    WriteDebugLog(
                        runId: "initial",
                        hypothesisId: "H29",
                        location: "EmbeddedPlaybackPreviewViewModel.cs:RefreshSegmentsAsync",
                        message: "Applied subtitle state during refresh",
                        data: new
                        {
                            activeSrtPath = _activeSrtPath,
                        });
                    // #endregion
                }
            }
            finally
            {
                _isUpdatingActiveSegment = false;
            }
            refreshStopwatch.Stop();
            // #region agent log
            WriteDebugLog(
                runId: "initial",
                hypothesisId: "H28",
                location: "EmbeddedPlaybackPreviewViewModel.cs:RefreshSegmentsAsync",
                message: "RefreshSegmentsAsync completed",
                data: new
                {
                    elapsedMs = refreshStopwatch.ElapsedMilliseconds,
                    segmentCount = Segments.Count,
                });
            // #endregion
        }
        catch (Exception ex)
        {
            _parent.StatusText = $"Failed to load segments: {ex.Message}";
            _parent.SetStatusErrorDetail("Load Segments failed", ex);
            // #region agent log
            WriteDebugLog(
                runId: "initial",
                hypothesisId: "H28",
                location: "EmbeddedPlaybackPreviewViewModel.cs:RefreshSegmentsAsync",
                message: "RefreshSegmentsAsync failed",
                data: new
                {
                    exceptionType = ex.GetType().FullName,
                    error = ex.Message,
                });
            // #endregion
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
                if (IsDubModeOn)
                    SyncDubToCurrentPosition(seekVideoToSegmentStart: true);

                await Task.Run(player.Play);
                IsSourcePaused = false;
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

    partial void OnAudioDuckingDbChanged(double value) => RecalculateOutputVolumes();

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
        }
        else
        {
            NotifyControlsActivity();
        }
    }

    partial void OnSegmentsChanged(ObservableCollection<WorkflowSegmentState> value)
    {
        // #region agent log
        WriteDebugLog(
            runId: "initial",
            hypothesisId: "H30",
            location: "EmbeddedPlaybackPreviewViewModel.cs:OnSegmentsChanged",
            message: "OnSegmentsChanged entered",
            data: new
            {
                valueCount = value.Count,
                managedThreadId = Environment.CurrentManagedThreadId,
            });
        // #endregion
        var sorted = value.ToArray();
        Array.Sort(sorted, (a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        _sortedSegments = sorted;
        // #region agent log
        WriteDebugLog(
            runId: "initial",
            hypothesisId: "H30",
            location: "EmbeddedPlaybackPreviewViewModel.cs:OnSegmentsChanged",
            message: "OnSegmentsChanged sorted segment cache",
            data: new
            {
                sortedCount = _sortedSegments.Length,
            });
        // #endregion
        _parent.SpeakerRouting.RebuildSpeakerIds(value, SelectedSegment?.SpeakerId);
        // #region agent log
        WriteDebugLog(
            runId: "initial",
            hypothesisId: "H30",
            location: "EmbeddedPlaybackPreviewViewModel.cs:OnSegmentsChanged",
            message: "OnSegmentsChanged completed",
            data: new
            {
                speakerCount = _parent.SpeakerRouting.SpeakerIds.Count,
            });
        // #endregion
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

    private async Task SeekAndPlayAsync(WorkflowSegmentState segment)
    {
        var player = _coordinator.SourceMediaPlayer;
        if (player is null)
        {
            await PlaySourceAtSegmentAsync(segment);
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

        if (IsDubModeOn && !IsSourcePaused)
            ApplyDubForSegment(segment);
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
        var sourceGain = _isDucked
            ? masterGain * Math.Pow(10.0, AudioDuckingDb / 20.0)
            : masterGain;

        _coordinator.SourceMediaPlayer?.Volume = sourceGain;
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

    private void ApplyDubForSegment(WorkflowSegmentState? segment, bool seekVideoToSegmentStart = false)
    {
        RestoreDucking();
        _coordinator.StopTtsPlayback();
        _lastDubbedSegment = segment;
        if (segment is null)
            return;

        if (seekVideoToSegmentStart)
            _coordinator.SourceMediaPlayer?.Seek((long)(segment.StartSeconds * 1000));

        if (segment.HasTtsAudio)
        {
            ApplyDucking();
            _ = _coordinator.PlayTtsForSegmentAsync(segment.SegmentId);
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
            var srt = SrtGenerator.Generate(Segments);
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
