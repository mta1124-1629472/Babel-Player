using System;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Babel.Player.Models;
using Babel.Player.Resources;
using Babel.Player.Services;
using Babel.Player.Services.Settings;
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
    private readonly object _ambianceTransportGate = new();
    private string? _lastKnownSourceMediaPath;
    private string? _lastKnownAmbianceAudioPath;
    private string? _loadedAmbiancePath;
    private string? _resolvedAmbiancePreviewPath;
    private DubPreviewAudioMode? _lastReportedDubPreviewMode;
    private string? _lastReportedAmbiancePath;
    private bool _isUpdatingPositionFromTimer;
    private bool _isUpdatingActiveSegment;
    private bool _isStartingAmbiancePlayback;
    private WorkflowSegmentState? _lastDubbedSegment;
    private WorkflowSegmentState[] _sortedSegments = [];
    private double _preMuteVolume = 1.0;
    private bool _isDucked;
    private string? _activeSrtPath;
    private ObservableCollection<WorkflowSegmentState>? _observedSegments;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _controlsHideTimer;
    private int _ambiancePlayRequestVersion;
    private const int ControlsHideDelayMs = 3000;
    private const double AmbianceSyncThresholdMs = 50.0;
    private const double PositionUpdateThresholdMs = 0.5;
    private const double PipelinePaneMinWidth = 220;
    private const double PipelinePaneMaxWidth = 420;
    private const double SegmentsPaneMinWidth = 280;
    private const double SegmentsPaneMaxWidth = 520;
    private const double PlayerPaneMinWidth = 460;
    private const double SplitterWidth = 5;
    private bool _isPipelinePaneVisible;
    private bool _isSegmentsPaneVisible;
    private double _pipelinePaneWidth;
    private double _segmentsPaneWidth;
    private bool _swapPaneSides;

    public EmbeddedPlaybackPreviewViewModel(
        EmbeddedPlaybackViewModel parent,
        SessionWorkflowCoordinator coordinator)
    {
        _parent = parent;
        _coordinator = coordinator;
        _lastKnownSourceMediaPath = coordinator.CurrentSession.SourceMediaPath;
        _lastKnownAmbianceAudioPath = coordinator.CurrentSession.AmbianceAudioPath;
        InvalidateAmbiancePreviewPathCache();
        _isSourceMediaLoaded = !string.IsNullOrEmpty(coordinator.CurrentSession.IngestedMediaPath);
        _speechRate = coordinator.TtsPlaybackRate;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += OnPositionTimerTick;
        _positionTimer.Start();

        _controlsHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ControlsHideDelayMs) };
        _controlsHideTimer.Tick += OnControlsHideTimerTick;

        _isBilingualSubtitlesOn = coordinator.CurrentSettings.BilingualSubtitlesEnabled;
        _isPipelinePaneVisible = coordinator.CurrentSettings.IsPipelinePaneVisible;
        _isSegmentsPaneVisible = coordinator.CurrentSettings.IsSegmentsPaneVisible;
        _pipelinePaneWidth = NormalizePipelinePaneWidth(coordinator.CurrentSettings.PipelinePaneWidth);
        _segmentsPaneWidth = NormalizeSegmentsPaneWidth(coordinator.CurrentSettings.SegmentsPaneWidth);
        _swapPaneSides = coordinator.CurrentSettings.SwapPaneSides;
        LocalizationService.Instance.CultureChanged += OnLocalizationCultureChanged;
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
    private bool _isFullscreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPullTabVisible))]
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

    public bool IsPullTabVisible => !IsFullscreen || IsControlsVisible;
    public string PlayPauseSourceLabel => IsSourcePaused ? "▶" : "||";
    public string VolumeIconLabel => IsMuted || SourceVolume == 0
        ? "🔇"
        : SourceVolume < 0.10
            ? "🔈"
            : SourceVolume < 0.51
                ? "🔉"
                : "🔊";
    public bool IsPipelinePaneVisible => _isPipelinePaneVisible;
    public bool IsSegmentsPaneVisible => _isSegmentsPaneVisible;
    public double PipelinePaneWidth => _pipelinePaneWidth;
    public double SegmentsPaneWidth => _segmentsPaneWidth;
    public bool SwapPaneSides => _swapPaneSides;
    public bool IsPipelinePaneOnLeft => !SwapPaneSides;
    public bool IsSegmentsPaneOnLeft => SwapPaneSides;
    public bool IsPipelinePaneShown => !IsFullscreen && IsPipelinePaneVisible;
    public bool IsSegmentsPaneShown => !IsFullscreen && IsSegmentsPaneVisible;
    public bool IsLeftPaneVisible => !IsFullscreen && (IsPipelinePaneOnLeft ? IsPipelinePaneVisible : IsSegmentsPaneVisible);
    public bool IsRightPaneVisible => !IsFullscreen && (IsPipelinePaneOnLeft ? IsSegmentsPaneVisible : IsPipelinePaneVisible);
    public double LeftPaneWidth => IsPipelinePaneOnLeft ? PipelinePaneWidth : SegmentsPaneWidth;
    public double RightPaneWidth => IsPipelinePaneOnLeft ? SegmentsPaneWidth : PipelinePaneWidth;
    public int PipelinePaneColumn => IsPipelinePaneOnLeft ? 0 : 4;
    public int SegmentsPaneColumn => IsSegmentsPaneOnLeft ? 0 : 4;
    public Thickness PipelinePaneMargin => IsPipelinePaneOnLeft ? new Thickness(8, 0, 0, 8) : new Thickness(0, 0, 8, 8);
    public Thickness SegmentsPaneMargin => IsSegmentsPaneOnLeft ? new Thickness(8, 0, 0, 8) : new Thickness(0, 0, 8, 8);
    public Thickness PipelinePaneBorderThickness => IsPipelinePaneOnLeft ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
    public Thickness SegmentsPaneBorderThickness => IsSegmentsPaneOnLeft ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
    public string LeftPaneRole => IsPipelinePaneOnLeft ? GetLocalized("Section_Pipeline") : GetLocalized("Section_Segments");
    public string RightPaneRole => IsPipelinePaneOnLeft ? GetLocalized("Section_Segments") : GetLocalized("Section_Pipeline");
    public string LeftPaneTooltip => BuildPaneTooltip(isLeftSide: true, LeftPaneRole, hotkey: "A");
    public string RightPaneTooltip => BuildPaneTooltip(isLeftSide: false, RightPaneRole, hotkey: "S");
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

    public void SyncPaneLayoutFromSettings()
    {
        var changed = false;
        changed |= SetPipelinePaneVisibleCore(_coordinator.CurrentSettings.IsPipelinePaneVisible);
        changed |= SetSegmentsPaneVisibleCore(_coordinator.CurrentSettings.IsSegmentsPaneVisible);
        changed |= SetPipelinePaneWidthCore(NormalizePipelinePaneWidth(_coordinator.CurrentSettings.PipelinePaneWidth));
        changed |= SetSegmentsPaneWidthCore(NormalizeSegmentsPaneWidth(_coordinator.CurrentSettings.SegmentsPaneWidth));
        changed |= SetSwapPaneSidesCore(_coordinator.CurrentSettings.SwapPaneSides);

        if (changed)
            NotifyPaneLayoutProjectionChanged();
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
    public bool UsesAmbianceMixControl => _resolvedAmbiancePreviewPath is not null;
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
    public string SegmentCountLabel => FormatSegmentCount(Segments.Count);

    public async Task HandleCurrentSessionChangedAsync()
    {
        var oldPath = _lastKnownSourceMediaPath;
        var oldAmbiancePath = _lastKnownAmbianceAudioPath;
        var newPath = _coordinator.CurrentSession.SourceMediaPath;
        var newAmbiancePath = _coordinator.CurrentSession.AmbianceAudioPath;
        InvalidateAmbiancePreviewPathCache();
        IsSourceMediaLoaded = !string.IsNullOrEmpty(_coordinator.CurrentSession.IngestedMediaPath);

        var sourceUnchanged = string.Equals(oldPath ?? "", newPath ?? "", StringComparison.OrdinalIgnoreCase);
        var ambianceUnchanged = string.Equals(oldAmbiancePath ?? "", newAmbiancePath ?? "", StringComparison.OrdinalIgnoreCase);

        if (!sourceUnchanged || !ambianceUnchanged)
        {
            PauseAmbiancePreview(resetLoadedPath: true);
            _lastReportedDubPreviewMode = null;
            _lastReportedAmbiancePath = null;
        }

        SyncDubMixControlFromSettings();
        _lastKnownAmbianceAudioPath = newAmbiancePath;

        if (newPath != oldPath)
        {
            _lastKnownSourceMediaPath = newPath;
            IsSourcePaused = true;
            _lastDubbedSegment = null;
            _isUpdatingActiveSegment = true;
            SelectedSegment = null;
            _isUpdatingActiveSegment = false;
        }

        if (sourceUnchanged && ambianceUnchanged && IsDubModeOn &&
            ResolveDubPreviewAudioMode() == DubPreviewAudioMode.SeparatedAmbiance)
        {
            SyncSeparatedAmbiancePreview(shouldPlay: !IsSourcePaused);
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
    private void ToggleLeftPane() => TogglePaneForSide(isLeftSide: true);

    [RelayCommand]
    private void ToggleRightPane() => TogglePaneForSide(isLeftSide: false);

    [RelayCommand]
    private void ResetLeftPaneWidth() => ResetPaneWidthForSide(isLeftSide: true);

    [RelayCommand]
    private void ResetRightPaneWidth() => ResetPaneWidthForSide(isLeftSide: false);

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
        LocalizationService.Instance.CultureChanged -= OnLocalizationCultureChanged;
        if (_observedSegments is not null)
            _observedSegments.CollectionChanged -= OnSegmentsCollectionChanged;
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
            RecalculateOutputVolumes();
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
            NotifyControlsActivity();
        }
        else
        {
            _controlsHideTimer.Stop();
            IsControlsVisible = true;
        }

        NotifyPaneLayoutProjectionChanged();
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
        if (_observedSegments is not null)
            _observedSegments.CollectionChanged -= OnSegmentsCollectionChanged;

        _observedSegments = value;
        _observedSegments.CollectionChanged += OnSegmentsCollectionChanged;
        OnPropertyChanged(nameof(SegmentCountLabel));

        var sorted = value.ToArray();
        Array.Sort(sorted, (a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        _sortedSegments = sorted;
        _parent.SpeakerRouting.RebuildSpeakerIds(value, SelectedSegment?.SpeakerId);
    }

    public void ResizeLeftPane(double desiredWidth, double hostWidth) =>
        ResizePaneForSide(isLeftSide: true, desiredWidth, hostWidth);

    public void ResizeRightPane(double desiredWidth, double hostWidth) =>
        ResizePaneForSide(isLeftSide: false, desiredWidth, hostWidth);

    public void CommitPaneLayout() => PersistPaneLayout();

    private void TogglePaneForSide(bool isLeftSide)
    {
        if (IsPipelineAssignedToSide(isLeftSide))
        {
            if (SetPipelinePaneVisibleCore(!GetPaneVisibilityForSide(isLeftSide)))
                PersistPaneLayout();
        }
        else if (SetSegmentsPaneVisibleCore(!GetPaneVisibilityForSide(isLeftSide)))
        {
            PersistPaneLayout();
        }

        NotifyPaneLayoutProjectionChanged();
    }

    private void ResetPaneWidthForSide(bool isLeftSide)
    {
        var changed = IsPipelineAssignedToSide(isLeftSide)
            ? SetPipelinePaneWidthCore(AppSettings.PipelinePaneDefaultWidth)
            : SetSegmentsPaneWidthCore(AppSettings.SegmentsPaneDefaultWidth);

        if (!changed)
            return;

        NotifyPaneLayoutProjectionChanged();
        PersistPaneLayout();
    }

    private void ResizePaneForSide(bool isLeftSide, double desiredWidth, double hostWidth)
    {
        var changed = IsPipelineAssignedToSide(isLeftSide)
            ? SetPipelinePaneWidthCore(ClampPaneWidthForRole(isPipelineRole: true, desiredWidth, hostWidth))
            : SetSegmentsPaneWidthCore(ClampPaneWidthForRole(isPipelineRole: false, desiredWidth, hostWidth));

        if (changed)
            NotifyPaneLayoutProjectionChanged();
    }

    private bool GetPaneVisibilityForSide(bool isLeftSide) =>
        IsPipelineAssignedToSide(isLeftSide) ? IsPipelinePaneVisible : IsSegmentsPaneVisible;

    private bool IsPipelineAssignedToSide(bool isLeftSide) =>
        isLeftSide ? IsPipelinePaneOnLeft : !IsPipelinePaneOnLeft;

    private double ClampPaneWidthForRole(bool isPipelineRole, double desiredWidth, double hostWidth)
    {
        var minWidth = isPipelineRole ? PipelinePaneMinWidth : SegmentsPaneMinWidth;
        var maxWidth = isPipelineRole ? PipelinePaneMaxWidth : SegmentsPaneMaxWidth;
        var normalizedDesired = Math.Clamp(desiredWidth, minWidth, maxWidth);

        if (hostWidth <= 0)
            return normalizedDesired;

        var otherPaneVisible = isPipelineRole ? IsSegmentsPaneVisible : IsPipelinePaneVisible;
        var otherPaneWidth = otherPaneVisible
            ? (isPipelineRole ? SegmentsPaneWidth : PipelinePaneWidth)
            : 0;
        var splitterCount = (IsPipelinePaneVisible ? 1 : 0) + (IsSegmentsPaneVisible ? 1 : 0);
        var maxByHost = hostWidth - PlayerPaneMinWidth - otherPaneWidth - (splitterCount * SplitterWidth);
        if (double.IsNaN(maxByHost) || double.IsInfinity(maxByHost))
            return normalizedDesired;

        return Math.Clamp(normalizedDesired, minWidth, Math.Min(maxWidth, Math.Max(minWidth, maxByHost)));
    }

    private bool SetPipelinePaneVisibleCore(bool value)
    {
        if (_isPipelinePaneVisible == value)
            return false;

        _isPipelinePaneVisible = value;
        OnPropertyChanged(nameof(IsPipelinePaneVisible));
        return true;
    }

    private bool SetSegmentsPaneVisibleCore(bool value)
    {
        if (_isSegmentsPaneVisible == value)
            return false;

        _isSegmentsPaneVisible = value;
        OnPropertyChanged(nameof(IsSegmentsPaneVisible));
        return true;
    }

    private bool SetPipelinePaneWidthCore(double value)
    {
        var normalized = NormalizePipelinePaneWidth(value);
        if (Math.Abs(_pipelinePaneWidth - normalized) < 0.01)
            return false;

        _pipelinePaneWidth = normalized;
        OnPropertyChanged(nameof(PipelinePaneWidth));
        return true;
    }

    private bool SetSegmentsPaneWidthCore(double value)
    {
        var normalized = NormalizeSegmentsPaneWidth(value);
        if (Math.Abs(_segmentsPaneWidth - normalized) < 0.01)
            return false;

        _segmentsPaneWidth = normalized;
        OnPropertyChanged(nameof(SegmentsPaneWidth));
        return true;
    }

    private bool SetSwapPaneSidesCore(bool value)
    {
        if (_swapPaneSides == value)
            return false;

        _swapPaneSides = value;
        OnPropertyChanged(nameof(SwapPaneSides));
        return true;
    }

    private void NotifyPaneLayoutProjectionChanged()
    {
        OnPropertyChanged(nameof(IsPipelinePaneOnLeft));
        OnPropertyChanged(nameof(IsSegmentsPaneOnLeft));
        OnPropertyChanged(nameof(IsPipelinePaneShown));
        OnPropertyChanged(nameof(IsSegmentsPaneShown));
        OnPropertyChanged(nameof(IsLeftPaneVisible));
        OnPropertyChanged(nameof(IsRightPaneVisible));
        OnPropertyChanged(nameof(LeftPaneWidth));
        OnPropertyChanged(nameof(RightPaneWidth));
        OnPropertyChanged(nameof(PipelinePaneColumn));
        OnPropertyChanged(nameof(SegmentsPaneColumn));
        OnPropertyChanged(nameof(PipelinePaneMargin));
        OnPropertyChanged(nameof(SegmentsPaneMargin));
        OnPropertyChanged(nameof(PipelinePaneBorderThickness));
        OnPropertyChanged(nameof(SegmentsPaneBorderThickness));
        OnPropertyChanged(nameof(LeftPaneRole));
        OnPropertyChanged(nameof(RightPaneRole));
        OnPropertyChanged(nameof(LeftPaneTooltip));
        OnPropertyChanged(nameof(RightPaneTooltip));
    }

    private void PersistPaneLayout()
    {
        _coordinator.CurrentSettings.IsPipelinePaneVisible = IsPipelinePaneVisible;
        _coordinator.CurrentSettings.IsSegmentsPaneVisible = IsSegmentsPaneVisible;
        _coordinator.CurrentSettings.PipelinePaneWidth = PipelinePaneWidth;
        _coordinator.CurrentSettings.SegmentsPaneWidth = SegmentsPaneWidth;
        _coordinator.CurrentSettings.SwapPaneSides = SwapPaneSides;
        _coordinator.NotifySettingsModified();
    }

    private static double NormalizePipelinePaneWidth(double width) =>
        Math.Clamp(width, PipelinePaneMinWidth, PipelinePaneMaxWidth);

    private static double NormalizeSegmentsPaneWidth(double width) =>
        Math.Clamp(width, SegmentsPaneMinWidth, SegmentsPaneMaxWidth);

    private string BuildPaneTooltip(bool isLeftSide, string roleLabel, string hotkey) =>
        string.Format(
            LocalizationService.Instance.CurrentCulture,
            GetLocalize