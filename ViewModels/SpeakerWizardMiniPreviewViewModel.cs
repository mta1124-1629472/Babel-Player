using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Babel.Player.Models;
using Babel.Player.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels;

/// <summary>
/// Wizard-local embedded libmpv preview (independent from the main window source player).
/// </summary>
public sealed partial class SpeakerWizardMiniPreviewViewModel : ViewModelBase, IDisposable
{
    private const double PositionUpdateThresholdMs = 0.5;

    private readonly SessionWorkflowCoordinator _coordinator;
    private readonly AppLog? _log;
    private LibMpvEmbeddedTransport? _player;
    private string? _lastLoadedPath;
    private bool _surfaceAttached;
    private bool _positionFromTimer;
    private bool _disposed;
    private readonly DispatcherTimer _positionTimer;

    public SpeakerWizardMiniPreviewViewModel(SessionWorkflowCoordinator coordinator)
    {
        _coordinator = coordinator;
        _log = coordinator.Log;
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += OnPositionTimerTick;
    }

    /// <summary>Windows-only native host for libmpv.</summary>
    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Show the embedded video host when the platform supports it.</summary>
    public bool ShowVideoSurface => IsWindows;

    public bool ShowNonWindowsHint => !IsWindows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionFormatted))]
    private double _positionMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationFormatted))]
    private double _durationMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiniPlayPauseLabel))]
    private bool _isPaused = true;

    [ObservableProperty]
    private bool _hasLoadedMedia;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusHint))]
    private string? _statusHint;

    public bool HasStatusHint => !string.IsNullOrWhiteSpace(StatusHint);

    public string MiniPlayPauseLabel => IsPaused ? "\u25B6 Play" : "Pause";

    /// <summary>When true, playhead-based reference extraction should use this preview's timeline.</summary>
    public bool UseMiniPlayheadForClips => HasLoadedMedia && IsWindows && string.IsNullOrEmpty(StatusHint);

    public string PositionFormatted => FormatMs(PositionMs);

    public string DurationFormatted => FormatMs(DurationMs);

    private static string FormatMs(double ms)
    {
        if (double.IsNaN(ms) || ms < 0)
            return "0:00";

        var total = (int)(ms / 1000.0);
        var m = total / 60;
        var s = total % 60;
        return $"{m}:{s:00}";
    }

    /// <summary>Attach to native surface and load current session ingested media.</summary>
    public void AttachAndLoad(IntPtr hwnd)
    {
        if (_disposed || !IsWindows)
            return;

        var path = _coordinator.CurrentSession.IngestedMediaPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusHint = "Load media in the main window first (no ingested file for this session).";
            HasLoadedMedia = false;
            return;
        }

        try
        {
            _player ??= new LibMpvEmbeddedTransport(
                new VideoPlaybackOptions(UseGpuNext: false, VsrEnabled: false),
                _log);

            _player.AttachToWindow(hwnd);
            _surfaceAttached = true;

            if (!string.Equals(path, _lastLoadedPath, StringComparison.OrdinalIgnoreCase))
            {
                _player.Load(path);
                _lastLoadedPath = path;
            }

            HasLoadedMedia = true;
            StatusHint = null;
            IsPaused = true;
            _positionTimer.Start();

            _positionFromTimer = true;
            PositionMs = _player.CurrentTime;
            DurationMs = _player.Duration;
            _positionFromTimer = false;
        }
        catch (Exception ex)
        {
            StatusHint = $"Mini preview: {ex.Message}";
            HasLoadedMedia = false;
        }
    }

    public void TryReloadAfterSessionChange()
    {
        if (_disposed || _player is null || !_surfaceAttached)
            return;

        var path = _coordinator.CurrentSession.IngestedMediaPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            HasLoadedMedia = false;
            StatusHint = "Ingested media is no longer available.";
            return;
        }

        if (string.Equals(path, _lastLoadedPath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _player.Load(path);
            _lastLoadedPath = path;
            HasLoadedMedia = true;
            StatusHint = null;
            IsPaused = true;
            _positionFromTimer = true;
            PositionMs = _player.CurrentTime;
            DurationMs = _player.Duration;
            _positionFromTimer = false;
        }
        catch (Exception ex)
        {
            StatusHint = ex.Message;
            HasLoadedMedia = false;
        }
    }

    public void SetViewport(Control videoView, Window window)
    {
        if (_player is null || _disposed)
            return;

        var scale = TopLevel.GetTopLevel(videoView)?.RenderScaling ?? 1.0;
        var width = Math.Max(0, (int)Math.Round(videoView.Bounds.Width * scale));
        var height = Math.Max(0, (int)Math.Round(videoView.Bounds.Height * scale));
        _player.SetDisplaySize(width, height);

        var screen = window.Screens.ScreenFromWindow(window);
        _player.SetMonitorResolution(screen?.Bounds.Width ?? 0, screen?.Bounds.Height ?? 0);
    }

    public void SeekToSegmentStart(WorkflowSegmentState segment)
    {
        if (!HasLoadedMedia || _player is null)
            return;

        _positionFromTimer = true;
        try
        {
            _player.Seek((long)(segment.StartSeconds * 1000));
            PositionMs = _player.CurrentTime;
            DurationMs = _player.Duration;
        }
        finally
        {
            _positionFromTimer = false;
        }
    }

    [RelayCommand]
    private void ToggleMiniPlayback()
    {
        if (!HasLoadedMedia || _player is null)
            return;

        try
        {
            if (IsPaused)
            {
                _coordinator.StopSourceMedia();
                Task.Run(() =>
                {
                    try
                    {
                        _player!.Play();
                        Dispatcher.UIThread.Post(() => IsPaused = false);
                    }
                    catch (Exception ex)
                    {
                        _log?.Warning($"Wizard mini preview play failed: {ex.Message}");
                        Dispatcher.UIThread.Post(() =>
                        {
                            StatusHint = ex.Message;
                            IsPaused = true;
                        });
                    }
                });
            }
            else
            {
                _player.Pause();
                IsPaused = true;
            }
        }
        catch (Exception ex)
        {
            StatusHint = ex.Message;
        }
    }

    partial void OnPositionMsChanged(double value)
    {
        if (_positionFromTimer || _player is null || !HasLoadedMedia)
            return;

        _player.Seek((long)value);
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        if (_player is null || !HasLoadedMedia)
            return;

        var p = _player.CurrentTime;
        var d = _player.Duration;
        if (Math.Abs(DurationMs - d) > PositionUpdateThresholdMs)
            DurationMs = d;

        if (Math.Abs(PositionMs - p) > PositionUpdateThresholdMs)
        {
            _positionFromTimer = true;
            PositionMs = p;
            _positionFromTimer = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTimerTick;

        try
        {
            _player?.Dispose();
        }
        catch
        {
            // best effort
        }

        _player = null;
    }
}
