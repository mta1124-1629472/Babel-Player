using System.Runtime.InteropServices;
using BabelPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Windows.Foundation;

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

    public MpvHostControl()
    {
        _root = new Grid();
        Content = _root;

        Loaded += MpvHostControl_Loaded;
        Unloaded += MpvHostControl_Unloaded;
        SizeChanged += MpvHostControl_SizeChanged;
        LayoutUpdated += MpvHostControl_LayoutUpdated;
        IsTabStop = false;

        _engine.OnStateChanged += snapshot => _snapshot = snapshot;
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
    public double PlaybackRate => _snapshot.Speed;
    public bool IsMuted => _snapshot.IsMuted;
    public bool IsPaused => _snapshot.IsPaused;
    public IReadOnlyList<MediaTrackInfo> CurrentTracks => _engine.CurrentTracks;
    public string ActiveHardwareDecoder => _snapshot.ActiveHardwareDecoder;
    public HardwareDecodingMode HardwareDecodingMode { get; set; } = HardwareDecodingMode.AutoSafe;

    public void Play() => _ = _engine.PlayAsync(_disposeCts.Token);
    public void Pause() => _ = _engine.PauseAsync(_disposeCts.Token);
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

    private void MpvHostControl_Loaded(object sender, RoutedEventArgs e) => EnsureInitialized();

    private void MpvHostControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _disposeCts.Cancel();
        _ = _engine.DisposeAsync();
        DestroyHostWindow();
    }

    private void MpvHostControl_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateHostBounds();

    private void MpvHostControl_LayoutUpdated(object? sender, object e) => UpdateHostBounds();

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

        _initialized = true;
        UpdateHostBounds();

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
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
            .Unwrap();
    }

    private void UpdateHostBounds()
    {
        if (_hwnd == IntPtr.Zero || XamlRoot?.Content is not UIElement rootContent || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var transform = TransformToVisual(rootContent);
        Point origin = transform.TransformPoint(new Point(0, 0));
        NativeMethods.MoveWindow(
            _hwnd,
            Math.Max((int)Math.Round(origin.X), 0),
            Math.Max((int)Math.Round(origin.Y), 0),
            Math.Max((int)Math.Round(ActualWidth), 1),
            Math.Max((int)Math.Round(ActualHeight), 1),
            true);
    }

    private void DestroyHostWindow()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private static class NativeMethods
    {
        public const int WS_CHILD = 0x40000000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPSIBLINGS = 0x04000000;
        public const int WS_CLIPCHILDREN = 0x02000000;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parentHandle, IntPtr menuHandle, IntPtr instanceHandle, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);
    }
}
