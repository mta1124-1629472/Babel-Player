using System.Runtime.InteropServices;
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
    private readonly Grid _root;
    private readonly MpvPlaybackEngine _engine = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private PlaybackStateSnapshot _snapshot = new();
    private Window? _ownerWindow;
    private Uri? _source;
    private string? _pendingPath;
    private double _volume = 0.8;
    private bool _initialized;
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
    private RectInt32 _lastHostBounds;
    private int _pendingHostBoundsRetryCount;

    public MpvHostControl()
    {
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
        _engine.OnStateChanged += snapshot => DispatcherQueue.TryEnqueue(() =>
        {
            _snapshot = snapshot;
            PlaybackStateChanged?.Invoke(snapshot);
            if (snapshot.VideoWidth > 0 && snapshot.VideoHeight > 0)
            {
                RequestHostBoundsSync();
            }
        });
        _engine.OnMediaOpened += () => DispatcherQueue.TryEnqueue(() => MediaOpened?.Invoke());
        _engine.OnMediaEnded += () => DispatcherQueue.TryEnqueue(() => MediaEnded?.Invoke());
        _engine.OnMediaFailed += message => DispatcherQueue.TryEnqueue(() => MediaFailed?.Invoke(message));
        _engine.OnTracksChanged += tracks => DispatcherQueue.TryEnqueue(() => TracksChanged?.Invoke(tracks));
        _engine.OnRuntimeInstallProgress += progress => DispatcherQueue.TryEnqueue(() => RuntimeInstallProgress?.Invoke(progress));
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

    public void Initialize(Window ownerWindow)
    {
        _ownerWindow = ownerWindow;
        if (IsLoaded)
        {
            EnsureInitialized();
        }
    }

    public Uri? Source
    {
        get => _source;
        set
        {
            _source = value;
            _pendingPath = value?.LocalPath;
            _snapshot = _snapshot with
            {
                Path = _pendingPath,
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero,
                VideoWidth = 0,
                VideoHeight = 0,
                VideoDisplayWidth = 0,
                VideoDisplayHeight = 0,
                HasVideo = false
            };
            PlaybackStateChanged?.Invoke(_snapshot);
            QueueHostBoundsSync();
            if (_initialized && !string.IsNullOrWhiteSpace(_pendingPath))
            {
                _ = _engine.LoadAsync(_pendingPath, _disposeCts.Token);
            }
        }
    }

    public TimeSpan Position
    {
        get => _snapshot.Position;
        set
        {
            if (_initialized)
            {
                _ = _engine.SeekAsync(value, _disposeCts.Token);
            }
        }
    }

    public double Volume
    {
        get => _snapshot.Volume > 0 ? _snapshot.Volume : _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_initialized)
            {
                _ = _engine.SetVolumeAsync(_volume, _disposeCts.Token);
            }
        }
    }

    public Duration NaturalDuration => _snapshot.Duration > TimeSpan.Zero ? new Duration(_snapshot.Duration) : Duration.Automatic;
    public PlaybackStateSnapshot Snapshot => _snapshot;
    public double PlaybackRate => _snapshot.Speed;
    public bool IsMuted => _snapshot.IsMuted;
    public bool IsPaused => _snapshot.IsPaused;
    public IReadOnlyList<MediaTrackInfo> CurrentTracks => _engine.CurrentTracks;
    public string ActiveHardwareDecoder => _snapshot.ActiveHardwareDecoder;
    public HardwareDecodingMode HardwareDecodingMode { get; set; } = HardwareDecodingMode.AutoSafe;

    public void Play() => _ = _engine.PlayAsync(_disposeCts.Token);
    public Task PlayAsync() => _engine.PlayAsync(_disposeCts.Token);
    public void Pause() => _ = _engine.PauseAsync(_disposeCts.Token);
    public Task PauseAsync() => _engine.PauseAsync(_disposeCts.Token);
    public void Stop() => _ = _engine.StopAsync(_disposeCts.Token);
    public void SeekBy(TimeSpan delta) => _ = _engine.SeekRelativeAsync(delta, _disposeCts.Token);
    public void StepFrame(bool forward) => _ = _engine.StepFrameAsync(forward, _disposeCts.Token);
    public void SetPlaybackRate(double speed) => _ = _engine.SetPlaybackRateAsync(speed, _disposeCts.Token);
    public void SetMute(bool muted) => _ = _engine.SetMuteAsync(muted, _disposeCts.Token);
    public void SelectAudioTrack(int? trackId) => _ = _engine.SetAudioTrackAsync(trackId, _disposeCts.Token);
    public void SelectSubtitleTrack(int? trackId) => _ = _engine.SetSubtitleTrackAsync(trackId, _disposeCts.Token);
    public void SetAudioDelay(double seconds) => _ = _engine.SetAudioDelayAsync(seconds, _disposeCts.Token);
    public void SetSubtitleDelay(double seconds) => _ = _engine.SetSubtitleDelayAsync(seconds, _disposeCts.Token);
    public void SetAspectRatio(string aspectRatio) => _ = _engine.SetAspectRatioAsync(aspectRatio, _disposeCts.Token);

    public void SetHardwareDecodingMode(HardwareDecodingMode mode)
    {
        HardwareDecodingMode = mode;
        if (_initialized)
        {
            _ = _engine.SetHardwareDecodingModeAsync(mode, _disposeCts.Token);
        }
    }

    public void SetZoom(double zoom) => _ = _engine.SetZoomAsync(zoom, _disposeCts.Token);
    public void SetPan(double x, double y) => _ = _engine.SetPanAsync(x, y, _disposeCts.Token);
    public void Screenshot(string outputPath) => _ = _engine.ScreenshotAsync(outputPath, _disposeCts.Token);
    public void RequestHostBoundsSync() => QueueHostBoundsSync();

    private void MpvHostControl_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureInitialized();
        QueueHostBoundsSync();
    }

    private void MpvHostControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _disposeCts.Cancel();
        _ = _engine.DisposeAsync();
        _hostBoundsRetryTimer.Stop();
        DestroyHostWindow();
    }

    private void MpvHostControl_SizeChanged(object sender, SizeChangedEventArgs e) => QueueHostBoundsSync();

    private void MpvHostControl_LayoutUpdated(object? sender, object e) => QueueHostBoundsSync();

    private void EnsureInitialized()
    {
        if (_initialized || _ownerWindow is null)
        {
            return;
        }

        var parentHwnd = WindowNative.GetWindowHandle(_ownerWindow);
        if (parentHwnd == IntPtr.Zero)
        {
            return;
        }

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
            MediaFailed?.Invoke("Unable to create the native WinUI video host window.");
            return;
        }

        _wndProc = HostWindowProc;
        _previousWndProc = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));

        _initialized = true;
        QueueHostBoundsSync();

        _ = _engine.InitializeAsync(_hwnd, HardwareDecodingMode, _disposeCts.Token)
            .ContinueWith(async task =>
            {
                if (task.IsFaulted)
                {
                    DispatcherQueue.TryEnqueue(() => MediaFailed?.Invoke(task.Exception?.GetBaseException().Message ?? "mpv initialization failed."));
                    return;
                }

                await _engine.SetVolumeAsync(_volume, _disposeCts.Token);
                if (!string.IsNullOrWhiteSpace(_pendingPath))
                {
                    await _engine.LoadAsync(_pendingPath, _disposeCts.Token);
                }

                DispatcherQueue.TryEnqueue(QueueHostBoundsSync);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
            .Unwrap();
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
        Point origin = transform.TransformPoint(new Point(0, 0));
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
        isVisible = Visibility == Visibility.Visible;
        return true;
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

    }
}
