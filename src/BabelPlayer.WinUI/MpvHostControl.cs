using System.Runtime.InteropServices;
using BabelPlayer.App;
using BabelPlayer.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace BabelPlayer.WinUI;

public sealed class MpvHostControl : UserControl
{
    private readonly IBabelLogger _logger;
    private readonly Grid _root;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Window? _ownerWindow;
    private IPlaybackBackend? _playbackBackend;
    private string? _pendingPath;
    private double _volume = 0.8;
    private bool _isMuted;
    private bool _initialized;
    private bool _eventsAttached;
    private IntPtr _hwnd;
    private IntPtr _previousWndProc;
    private NativeMethods.WndProc? _wndProc;
    private long _lastInputActivityTick;
    private long _lastClickTick;
    private readonly DispatcherTimer _hostBoundsRetryTimer;
    private bool _hostBoundsSyncQueued;
    private bool _hasLastHostBounds;
    private bool _lastHostVisible;
    private bool _hasLastHostVisibility;
    private int _nativeHostSuppressionCount;
    private RectInt32 _lastHostBounds;
    private int _pendingHostBoundsRetryCount;

    public MpvHostControl(IBabelLogFactory? logFactory = null)
    {
        _logger = (logFactory ?? NullBabelLogFactory.Instance).CreateLogger("shell.mpvHost");
        _root = new Grid();
        Content = _root;
        _hostBoundsRetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(32)
        };

        Loaded += MpvHostControl_Loaded;
        Unloaded += MpvHostControl_Unloaded;
        SizeChanged += MpvHostControl_SizeChanged;
        LayoutUpdated += MpvHostControl_LayoutUpdated;
        IsTabStop = false;

        _hostBoundsRetryTimer.Tick += HostBoundsRetryTimer_Tick;
    }

    public event Action? MediaOpened;
    public event Action? MediaEnded;
    public event Action<string>? MediaFailed;
    public event Action<IReadOnlyList<MediaTrackInfo>>? TracksChanged;
    public event Action<RuntimeInstallProgress>? RuntimeInstallProgress;
    public event Action<PlaybackStateSnapshot>? PlaybackStateChanged;
    public event Action? InputActivity;
    public event Action? FullscreenExitRequested;
    public event Func<ShortcutKeyInput, bool>? ShortcutKeyPressed;

    public void Initialize(Window ownerWindow, IPlaybackBackend playbackBackend)
    {
        ArgumentNullException.ThrowIfNull(ownerWindow);
        ArgumentNullException.ThrowIfNull(playbackBackend);

        _ownerWindow = ownerWindow;
        AttachPlaybackBackend(playbackBackend);
        if (IsLoaded)
        {
            EnsureInitialized();
        }
    }

    public Uri? Source
    {
        get => string.IsNullOrWhiteSpace(_playbackBackend?.State.Path) ? null : new Uri(_playbackBackend.State.Path);
        set
        {
            _pendingPath = value?.LocalPath;
            if (_initialized && _playbackBackend is not null && !string.IsNullOrWhiteSpace(_pendingPath))
            {
                _ = _playbackBackend.LoadAsync(_pendingPath, _lifetimeCts.Token);
            }

            RaisePlaybackStateChanged();
        }
    }

    public TimeSpan Position
    {
        get => _playbackBackend?.Clock.Current.Position ?? TimeSpan.Zero;
        set
        {
            if (_initialized && _playbackBackend is not null)
            {
                _ = _playbackBackend.SeekAsync(value, _lifetimeCts.Token);
            }
        }
    }

    public double Volume
    {
        get => _initialized && _playbackBackend is not null
            ? Math.Clamp(_playbackBackend.State.Volume, 0, 1)
            : _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_initialized && _playbackBackend is not null)
            {
                _ = _playbackBackend.SetVolumeAsync(_volume, _lifetimeCts.Token);
            }
        }
    }

    public Duration NaturalDuration
    {
        get
        {
            var duration = _playbackBackend?.Clock.Current.Duration ?? TimeSpan.Zero;
            return duration > TimeSpan.Zero ? new Duration(duration) : Duration.Automatic;
        }
    }

    public PlaybackStateSnapshot Snapshot => BuildPlaybackSnapshot();

    public double PlaybackRate => _playbackBackend?.Clock.Current.Rate ?? 1.0;

    public bool IsMuted => _initialized && _playbackBackend is not null
        ? _playbackBackend.State.IsMuted
        : _isMuted;

    public bool IsPaused => _playbackBackend?.Clock.Current.IsPaused ?? true;

    public IReadOnlyList<MediaTrackInfo> CurrentTracks => _playbackBackend?.CurrentTracks ?? [];

    public string ActiveHardwareDecoder => _playbackBackend?.State.ActiveHardwareDecoder ?? string.Empty;

    public HardwareDecodingMode HardwareDecodingMode
    {
        get => _playbackBackend?.HardwareDecodingMode ?? HardwareDecodingMode.AutoSafe;
        set
        {
            if (_playbackBackend is not null)
            {
                _ = _playbackBackend.SetHardwareDecodingModeAsync(value, _lifetimeCts.Token);
            }
        }
    }

    public void Play() => _ = _playbackBackend?.PlayAsync(_lifetimeCts.Token);

    public Task PlayAsync() => _playbackBackend?.PlayAsync(_lifetimeCts.Token) ?? Task.CompletedTask;

    public void Pause() => _ = _playbackBackend?.PauseAsync(_lifetimeCts.Token);

    public Task PauseAsync() => _playbackBackend?.PauseAsync(_lifetimeCts.Token) ?? Task.CompletedTask;

    public void Stop() => _ = _playbackBackend?.StopAsync(_lifetimeCts.Token);

    public void SeekBy(TimeSpan delta) => _ = _playbackBackend?.SeekRelativeAsync(delta, _lifetimeCts.Token);

    public void StepFrame(bool forward) => _ = _playbackBackend?.StepFrameAsync(forward, _lifetimeCts.Token);

    public void SetPlaybackRate(double speed) => _ = _playbackBackend?.SetPlaybackRateAsync(speed, _lifetimeCts.Token);

    public void SetMute(bool muted)
    {
        _isMuted = muted;
        _ = _playbackBackend?.SetMuteAsync(muted, _lifetimeCts.Token);
    }

    public void SetPreferredAudioState(double volume, bool muted)
    {
        _volume = Math.Clamp(volume, 0, 1);
        _isMuted = muted;
    }

    public void SelectAudioTrack(int? trackId) => _ = _playbackBackend?.SetAudioTrackAsync(trackId, _lifetimeCts.Token);

    public void SelectSubtitleTrack(int? trackId) => _ = _playbackBackend?.SetSubtitleTrackAsync(trackId, _lifetimeCts.Token);

    public void SetAudioDelay(double seconds) => _ = _playbackBackend?.SetAudioDelayAsync(seconds, _lifetimeCts.Token);

    public void SetSubtitleDelay(double seconds) => _ = _playbackBackend?.SetSubtitleDelayAsync(seconds, _lifetimeCts.Token);

    public void SetAspectRatio(string aspectRatio) => _ = _playbackBackend?.SetAspectRatioAsync(aspectRatio, _lifetimeCts.Token);

    public void SetHardwareDecodingMode(HardwareDecodingMode mode) => _ = _playbackBackend?.SetHardwareDecodingModeAsync(mode, _lifetimeCts.Token);

    public void SetZoom(double zoom) => _ = _playbackBackend?.SetZoomAsync(zoom, _lifetimeCts.Token);

    public void SetPan(double x, double y) => _ = _playbackBackend?.SetPanAsync(x, y, _lifetimeCts.Token);

    public void Screenshot(string outputPath) => _ = _playbackBackend?.ScreenshotAsync(outputPath, _lifetimeCts.Token);

    public void RequestHostBoundsSync() => QueueHostBoundsSync();

    public IDisposable SuppressNativeHost() => new NativeHostSuppressionScope(this);

    public RectInt32 GetStageBounds(FrameworkElement relativeTo)
    {
        ArgumentNullException.ThrowIfNull(relativeTo);
        if (XamlRoot is null || _ownerWindow is null)
        {
            return default;
        }

        var hwnd = WindowNative.GetWindowHandle(_ownerWindow);
        var topLeft = new NativePoint();
        if (!NativeMethods.ClientToScreen(hwnd, ref topLeft))
        {
            return default;
        }

        var transform = TransformToVisual(relativeTo);
        var origin = transform.TransformPoint(new Point(0, 0));
        var scale = XamlRoot.RasterizationScale;
        var x = topLeft.X + Math.Max((int)Math.Round(origin.X * scale), 0);
        var y = topLeft.Y + Math.Max((int)Math.Round(origin.Y * scale), 0);
        var width = Math.Max((int)Math.Round(ActualWidth * scale), 0);
        var height = Math.Max((int)Math.Round(ActualHeight * scale), 0);
        return width <= 0 || height <= 0
            ? default
            : new RectInt32(x, y, width, height);
    }

    private void AttachPlaybackBackend(IPlaybackBackend playbackBackend)
    {
        if (ReferenceEquals(_playbackBackend, playbackBackend) && _eventsAttached)
        {
            return;
        }

        DetachPlaybackBackend();
        _playbackBackend = playbackBackend;
        _playbackBackend.StateChanged += PlaybackBackend_StateChanged;
        _playbackBackend.TracksChanged += PlaybackBackend_TracksChanged;
        _playbackBackend.MediaOpened += PlaybackBackend_MediaOpened;
        _playbackBackend.MediaEnded += PlaybackBackend_MediaEnded;
        _playbackBackend.MediaFailed += PlaybackBackend_MediaFailed;
        _playbackBackend.RuntimeInstallProgress += PlaybackBackend_RuntimeInstallProgress;
        _playbackBackend.Clock.Changed += PlaybackClock_Changed;
        _eventsAttached = true;
    }

    private void DetachPlaybackBackend()
    {
        if (_playbackBackend is null || !_eventsAttached)
        {
            return;
        }

        _playbackBackend.StateChanged -= PlaybackBackend_StateChanged;
        _playbackBackend.TracksChanged -= PlaybackBackend_TracksChanged;
        _playbackBackend.MediaOpened -= PlaybackBackend_MediaOpened;
        _playbackBackend.MediaEnded -= PlaybackBackend_MediaEnded;
        _playbackBackend.MediaFailed -= PlaybackBackend_MediaFailed;
        _playbackBackend.RuntimeInstallProgress -= PlaybackBackend_RuntimeInstallProgress;
        _playbackBackend.Clock.Changed -= PlaybackClock_Changed;
        _eventsAttached = false;
    }

    private void MpvHostControl_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureInitialized();
        QueueHostBoundsSync();
    }

    private void MpvHostControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _lifetimeCts.Cancel();
        _hostBoundsRetryTimer.Stop();
        DestroyHostWindow();
    }

    private void MpvHostControl_SizeChanged(object sender, SizeChangedEventArgs e) => QueueHostBoundsSync();

    private void MpvHostControl_LayoutUpdated(object? sender, object e) => QueueHostBoundsSync();

    private void EnsureInitialized()
    {
        if (_initialized || _ownerWindow is null || _playbackBackend is null)
        {
            return;
        }

        var parentHwnd = WindowNative.GetWindowHandle(_ownerWindow);
        if (parentHwnd == IntPtr.Zero)
        {
            return;
        }

        _logger.LogInfo(
            "Initializing mpv host control.",
            BabelLogContext.Create(
                ("pendingPath", _pendingPath),
                ("preferredVolume", _volume),
                ("preferredMuted", _isMuted)));

        _hwnd = NativeMethods.CreateWindowEx(
            0,
            "static",
            string.Empty,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN,
            0,
            0,
            Math.Max((int)ActualWidth, 1),
            Math.Max((int)ActualHeight, 1),
            parentHwnd,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            _logger.LogError("Unable to create native mpv host window.");
            MediaFailed?.Invoke("Unable to create the native WinUI video host window.");
            return;
        }

        _wndProc = HostWindowProc;
        _previousWndProc = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));

        _initialized = true;
        QueueHostBoundsSync();

        _ = _playbackBackend.InitializeAsync(_hwnd, _lifetimeCts.Token)
            .ContinueWith(async task =>
            {
                if (task.IsFaulted)
                {
                    _logger.LogError(
                        "Playback backend initialization failed from host control.",
                        task.Exception?.GetBaseException(),
                        BabelLogContext.Create(("pendingPath", _pendingPath)));
                    DispatcherQueue.TryEnqueue(() => MediaFailed?.Invoke(task.Exception?.GetBaseException().Message ?? "Playback backend initialization failed."));
                    return;
                }

                await SyncPreferredAudioStateAsync("backend-initialized").ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(_pendingPath))
                {
                    await _playbackBackend.LoadAsync(_pendingPath, _lifetimeCts.Token);
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    _logger.LogInfo(
                        "Playback backend initialized for mpv host control.",
                        BabelLogContext.Create(("pendingPath", _pendingPath), ("hostWindow", _hwnd)));
                    RaisePlaybackStateChanged();
                    QueueHostBoundsSync();
                });
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
            .Unwrap();
    }

    private void PlaybackBackend_StateChanged(PlaybackBackendState state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (state.VideoWidth > 0 && state.VideoHeight > 0)
            {
                RequestHostBoundsSync();
            }

            RaisePlaybackStateChanged();
        });
    }

    private void PlaybackBackend_TracksChanged(IReadOnlyList<MediaTrackInfo> tracks)
    {
        DispatcherQueue.TryEnqueue(() => TracksChanged?.Invoke(tracks));
    }

    private void PlaybackBackend_MediaOpened()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = SyncPreferredAudioStateAsync("media-opened");
            RaisePlaybackStateChanged();
            MediaOpened?.Invoke();
        });
    }

    private void PlaybackBackend_MediaEnded()
    {
        DispatcherQueue.TryEnqueue(() => MediaEnded?.Invoke());
    }

    private void PlaybackBackend_MediaFailed(string message)
    {
        DispatcherQueue.TryEnqueue(() => MediaFailed?.Invoke(message));
    }

    private void PlaybackBackend_RuntimeInstallProgress(RuntimeInstallProgress progress)
    {
        DispatcherQueue.TryEnqueue(() => RuntimeInstallProgress?.Invoke(progress));
    }

    private void PlaybackClock_Changed(ClockSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(RaisePlaybackStateChanged);
    }

    private void RaisePlaybackStateChanged()
    {
        PlaybackStateChanged?.Invoke(BuildPlaybackSnapshot());
    }

    private PlaybackStateSnapshot BuildPlaybackSnapshot()
    {
        var clock = _playbackBackend?.Clock.Current ?? new ClockSnapshot(TimeSpan.Zero, TimeSpan.Zero, 1.0, true, false, DateTimeOffset.UtcNow);
        var state = _playbackBackend?.State ?? new PlaybackBackendState();
        return new PlaybackStateSnapshot
        {
            Path = state.Path ?? _pendingPath,
            Position = clock.Position,
            Duration = clock.Duration,
            Speed = clock.Rate,
            IsPaused = clock.IsPaused,
            IsSeekable = clock.IsSeekable,
            HasVideo = state.HasVideo,
            HasAudio = state.HasAudio,
            VideoWidth = state.VideoWidth,
            VideoHeight = state.VideoHeight,
            VideoDisplayWidth = state.VideoDisplayWidth,
            VideoDisplayHeight = state.VideoDisplayHeight,
            IsMuted = state.IsMuted,
            Volume = state.Volume,
            ActiveHardwareDecoder = state.ActiveHardwareDecoder
        };
    }

    private async Task SyncPreferredAudioStateAsync(string reason)
    {
        if (!_initialized || _playbackBackend is null || _lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _logger.LogInfo(
                "Synchronizing preferred audio state.",
                BabelLogContext.Create(
                    ("reason", reason),
                    ("volume", _volume),
                    ("muted", _isMuted),
                    ("path", _playbackBackend.State.Path)));
            await _playbackBackend.SetVolumeAsync(_volume, _lifetimeCts.Token).ConfigureAwait(false);
            await _playbackBackend.SetMuteAsync(_isMuted, _lifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Failed to synchronize preferred audio state.",
                ex,
                BabelLogContext.Create(
                    ("reason", reason),
                    ("volume", _volume),
                    ("muted", _isMuted),
                    ("path", _playbackBackend.State.Path)));
        }
    }

    private void QueueHostBoundsSync()
    {
        if (_hostBoundsSyncQueued)
        {
            return;
        }

        _hostBoundsSyncQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _hostBoundsSyncQueued = false;
            UpdateHostBounds();
        });
    }

    private void UpdateHostBounds()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!TryGetHostBounds(out var bounds, out var isVisible))
        {
            ScheduleDeferredHostBoundsRetry();
            return;
        }

        _pendingHostBoundsRetryCount = 0;
        _hostBoundsRetryTimer.Stop();
        if (_hasLastHostBounds &&
            _hasLastHostVisibility &&
            bounds.Equals(_lastHostBounds) &&
            isVisible == _lastHostVisible)
        {
            return;
        }

        _logger.LogInfo(
            "Native video host bounds synchronized.",
            BabelLogContext.Create(
                ("x", bounds.X),
                ("y", bounds.Y),
                ("width", bounds.Width),
                ("height", bounds.Height),
                ("visible", isVisible),
                ("path", _playbackBackend?.State.Path)));

        NativeMethods.ShowWindow(_hwnd, isVisible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        if (isVisible)
        {
            NativeMethods.MoveWindow(_hwnd, bounds.X, bounds.Y, bounds.Width, bounds.Height, true);
        }

        _lastHostBounds = bounds;
        _hasLastHostBounds = true;
        _lastHostVisible = isVisible;
        _hasLastHostVisibility = true;
    }

    private bool TryGetHostBounds(out RectInt32 bounds, out bool isVisible)
    {
        bounds = default;
        isVisible = false;
        if (!_initialized || XamlRoot?.Content is not UIElement rootContent || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return false;
        }

        var transform = TransformToVisual(rootContent);
        var origin = transform.TransformPoint(new Point(0, 0));
        var scale = XamlRoot.RasterizationScale;
        var width = Math.Max((int)Math.Round(ActualWidth * scale), 0);
        var height = Math.Max((int)Math.Round(ActualHeight * scale), 0);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        bounds = new RectInt32(
            Math.Max((int)Math.Round(origin.X * scale), 0),
            Math.Max((int)Math.Round(origin.Y * scale), 0),
            Math.Max(width, 1),
            Math.Max(height, 1));
        isVisible = Visibility == Visibility.Visible && _nativeHostSuppressionCount == 0;
        return true;
    }

    private void BeginNativeHostSuppression()
    {
        _nativeHostSuppressionCount++;
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        }

        _hasLastHostVisibility = false;
        QueueHostBoundsSync();
    }

    private void EndNativeHostSuppression()
    {
        if (_nativeHostSuppressionCount == 0)
        {
            return;
        }

        _nativeHostSuppressionCount--;
        _hasLastHostVisibility = false;
        QueueHostBoundsSync();
    }

    private void ScheduleDeferredHostBoundsRetry()
    {
        if (_pendingHostBoundsRetryCount >= 20)
        {
            return;
        }

        _pendingHostBoundsRetryCount++;
        _hostBoundsRetryTimer.Stop();
        _hostBoundsRetryTimer.Start();
    }

    private void HostBoundsRetryTimer_Tick(object? sender, object e)
    {
        _hostBoundsRetryTimer.Stop();
        QueueHostBoundsSync();
    }

    private void DestroyHostWindow()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_previousWndProc != IntPtr.Zero)
        {
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_WNDPROC, _previousWndProc);
            _previousWndProc = IntPtr.Zero;
        }

        NativeMethods.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _wndProc = null;
    }

    private IntPtr HostWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_MOUSEMOVE:
                if (ShouldRaiseInputActivity())
                {
                    InputActivity?.Invoke();
                }

                break;
            case NativeMethods.WM_LBUTTONDOWN:
                InputActivity?.Invoke();
                if (IsManualDoubleClick())
                {
                    FullscreenExitRequested?.Invoke();
                }

                break;
            case NativeMethods.WM_LBUTTONDBLCLK:
                InputActivity?.Invoke();
                FullscreenExitRequested?.Invoke();
                _lastClickTick = 0;
                break;
            case NativeMethods.WM_KEYDOWN:
            case NativeMethods.WM_SYSKEYDOWN:
                InputActivity?.Invoke();
                if ((int)wParam == NativeMethods.VK_ESCAPE)
                {
                    FullscreenExitRequested?.Invoke();
                    return IntPtr.Zero;
                }

                if (RaiseShortcutKeyPressed((VirtualKey)wParam.ToInt32()))
                {
                    return IntPtr.Zero;
                }

                break;
            case NativeMethods.WM_NCDESTROY:
                if (_previousWndProc != IntPtr.Zero)
                {
                    NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_WNDPROC, _previousWndProc);
                    _previousWndProc = IntPtr.Zero;
                }

                break;
        }

        return _previousWndProc != IntPtr.Zero
            ? NativeMethods.CallWindowProc(_previousWndProc, hwnd, message, wParam, lParam)
            : NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private bool ShouldRaiseInputActivity()
    {
        var now = Environment.TickCount64;
        if (now - _lastInputActivityTick < 75)
        {
            return false;
        }

        _lastInputActivityTick = now;
        return true;
    }

    private bool IsManualDoubleClick()
    {
        var now = Environment.TickCount64;
        var threshold = NativeMethods.GetDoubleClickTime();
        var isDoubleClick = _lastClickTick > 0 && now - _lastClickTick <= threshold;
        _lastClickTick = now;
        return isDoubleClick;
    }

    private bool RaiseShortcutKeyPressed(VirtualKey key)
    {
        if (ShortcutKeyPressed is null)
        {
            return false;
        }

        var input = new ShortcutKeyInput(
            key,
            IsModifierPressed(NativeMethods.VK_CONTROL),
            IsModifierPressed(NativeMethods.VK_MENU),
            IsModifierPressed(NativeMethods.VK_SHIFT));

        foreach (Func<ShortcutKeyInput, bool> handler in ShortcutKeyPressed.GetInvocationList())
        {
            if (handler(input))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsModifierPressed(int virtualKey) => (NativeMethods.GetKeyState(virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private static class NativeMethods
    {
        public const int WS_CHILD = 0x40000000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPSIBLINGS = 0x04000000;
        public const int WS_CLIPCHILDREN = 0x02000000;
        public const int GWLP_WNDPROC = -4;
        public const int VK_ESCAPE = 0x1B;
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;
        public const uint WM_MOUSEMOVE = 0x0200;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONDBLCLK = 0x0203;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_SYSKEYDOWN = 0x0104;
        public const uint WM_NCDESTROY = 0x0082;
        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;

        public delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parentHandle, IntPtr menuHandle, IntPtr instanceHandle, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newProc);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = false)]
        public static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(nint hWnd, ref NativePoint lpPoint);
    }

    private sealed class NativeHostSuppressionScope : IDisposable
    {
        private MpvHostControl? _owner;

        public NativeHostSuppressionScope(MpvHostControl owner)
        {
            _owner = owner;
            owner.BeginNativeHostSuppression();
        }

        public void Dispose()
        {
            if (_owner is null)
            {
                return;
            }

            _owner.EndNativeHostSuppression();
            _owner = null;
        }
    }
}
