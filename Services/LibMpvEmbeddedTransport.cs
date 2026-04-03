using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

using Babel.Player.Models;

namespace Babel.Player.Services;

/// <summary>
/// Embedded media transport using libmpv with GPU-accelerated rendering into a native window.
/// Unlike LibMpvHeadlessTransport, this renders video into an HWND provided via the --wid option,
/// using libmpv's own GPU pipeline (OpenGL/D3D11 under the hood).
///
/// When <see cref="VideoPlaybackOptions.UseGpuNext"/> is true the transport switches to the
/// gpu-next video output backend, which is required for RTX Video Super Resolution and the
/// correct mpv HDR pipeline.
///
/// VSR is applied dynamically when a file finishes loading: a background event-polling thread
/// detects the MPV_EVENT_FILE_LOADED event (id 8), queries the video dimensions, computes the
/// upscale factor required to reach the display resolution, and issues the d3d11vpp filter
/// command. The transport logs whether the filter was applied or skipped so RTX playback
/// diagnostics distinguish requested state from active state.
/// </summary>
public class LibMpvEmbeddedTransport : IMediaTransport, IDisposable
{
    private IntPtr _handle = IntPtr.Zero;
    private bool _disposed;
    private bool _isLoaded;
    private bool _isPaused;
    private bool _hasEnded;

    // Options captured at construction for VSR application on file-load.
    private readonly VideoPlaybackOptions _options;
    private readonly AppLog? _log;

    // Background event-loop thread for MPV_EVENT_FILE_LOADED.
    private Thread? _eventThread;
    private volatile bool _eventThreadRunning;

    // Display dimensions supplied by the host (set via SetDisplaySize).
    private int _displayWidth;
    private int _displayHeight;
    private string? _lastVsrDiagnosticKey;

    private delegate IntPtr mpv_create_delegate();
    private delegate int mpv_initialize_delegate(IntPtr handle);
    private delegate int mpv_set_option_string_delegate(IntPtr handle, string name, string value);
    private delegate int mpv_command_string_delegate(IntPtr handle, string command);
    private delegate IntPtr mpv_get_property_string_delegate(IntPtr handle, string name);
    private delegate void mpv_free_delegate(IntPtr data);
    private delegate void mpv_terminate_destroy_delegate(IntPtr handle);
    private delegate IntPtr mpv_wait_event_delegate(IntPtr handle, double timeout);

    private mpv_create_delegate? _mpv_create;
    private mpv_initialize_delegate? _mpv_initialize;
    private mpv_set_option_string_delegate? _mpv_set_option_string;
    private mpv_command_string_delegate? _mpv_command_string;
    private mpv_get_property_string_delegate? _mpv_get_property_string;
    private mpv_free_delegate? _mpv_free;
    private mpv_terminate_destroy_delegate? _mpv_terminate_destroy;
    private mpv_wait_event_delegate? _mpv_wait_event;

    private IntPtr _dllHandle = IntPtr.Zero;

    /// <summary>
    /// The native window handle (HWND) that libmpv renders video into.
    /// Must be set before calling <see cref="Load"/> if video rendering is desired.
    /// If null/zero, behaves like a headless transport with audio.
    /// </summary>
    public IntPtr WindowHandle { get; set; }

#pragma warning disable CS0067
    public event EventHandler? Ended;
    public event EventHandler<Exception>? ErrorOccurred;
#pragma warning restore CS0067

    public LibMpvEmbeddedTransport(VideoPlaybackOptions? options = null, AppLog? log = null)
    {
        _options = options ?? new VideoPlaybackOptions();
        _log = log;

        _dllHandle = LoadLibMpvDll();
        if (_dllHandle == IntPtr.Zero)
            throw new DllNotFoundException("libmpv DLL not found.");

        LoadLibMpvFunctions();

        _handle = _mpv_create!();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create libmpv context.");

        // ── Video output backend ───────────────────────────────────────────────
        // gpu-next is required for RTX VSR (d3d11vpp filter) and for the correct
        // HDR pipeline (target-colorspace-hint).  It is opt-in: the legacy "gpu"
        // backend is used when UseGpuNext is false.
        if (_options.UseGpuNext)
        {
            SetOption("vo", "gpu-next");
            // Pin the rendering context to D3D11 so d3d11vpp is available.
            SetOption("gpu-context", "d3d11");
        }
        else
        {
            SetOption("vo", "gpu");
        }

        // Hardware decode and GPU API — set before mpv_initialize; cannot change at runtime
        SetOption("hwdec",   _options.HwdecMode);
        SetOption("gpu-api", _options.GpuApi);

        // ── HDR pipeline options (gpu-next + HDR display required) ────────────
        if (_options.UseGpuNext && _options.HdrEnabled)
        {
            // Request mpv's HDR-capable output path. NVIDIA RTX HDR still depends on
            // Windows HDR state and whether the playback surface is one NVIDIA hooks.
            SetOption("target-colorspace-hint", "yes");
            // Tone-mapping algorithm for HDR → display peak mapping.
            SetOption("tone-mapping", _options.ToneMapping);
            // Display peak nit target ("auto" or a numeric string like "1000").
            if (!string.IsNullOrWhiteSpace(_options.TargetPeak))
                SetOption("target-peak", _options.TargetPeak);
            // Dynamic per-frame peak detection — may cause brightness instability.
            SetOption("hdr-compute-peak", _options.HdrComputePeak ? "yes" : "no");
            // Honour the display ICC profile for accurate colour.
            SetOption("icc-profile-auto", "yes");
        }

        // Keep audio enabled for source media preview
        SetOption("idle", "yes");
        SetOption("keep-open", "yes");
        // Start paused so the user controls when playback begins
        SetOption("pause", "yes");
        // Suppress native mpv OSD — seek bar and volume overlay are shown by Avalonia controls
        SetOption("osd-level", "0");

        if (_mpv_initialize!(_handle) != 0)
            throw new InvalidOperationException("Failed to initialize libmpv.");

        _isLoaded = false;
        _isPaused = true;
        _hasEnded = false;

        if (_options.UseGpuNext && _options.HdrEnabled)
        {
            _log?.Info(
                $"Configured mpv HDR output path: gpu-next={_options.UseGpuNext}, " +
                $"tone_mapping={_options.ToneMapping}, target_peak='{_options.TargetPeak}', " +
                "note=driver-level RTX HDR activation still depends on a supported NVIDIA playback path.");
        }

        // Start the background event loop only when VSR may be applied.
        if (_options.UseGpuNext && _options.VsrEnabled)
            StartEventThread();
    }

    /// <summary>
    /// Informs the transport of the native render-target dimensions so that the VSR
    /// upscale factor can be computed correctly when a file loads.
    /// Call this after the HWND is created and whenever the window is resized.
    /// </summary>
    public void SetDisplaySize(int width, int height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);

        if (_displayWidth == width && _displayHeight == height)
            return;

        _displayWidth = width;
        _displayHeight = height;

        if (_options.UseGpuNext && _options.VsrEnabled)
        {
            _log?.Info($"Updated embedded video surface size: width={width}, height={height}");

            if (_isLoaded && _handle != IntPtr.Zero)
                ApplyVsrFilter("display-size-updated");
        }
    }

    /// <summary>
    /// Attaches the libmpv render output to the given native window handle.
    /// Must be called after construction and before Load for video to appear.
    /// </summary>
    public void AttachToWindow(IntPtr hwnd)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LibMpvEmbeddedTransport));
        WindowHandle = hwnd;
        // Set wid property — libmpv will render into this window
        var widStr = hwnd.ToInt64().ToString();
        _mpv_command_string!(_handle, $"set wid {widStr}");
    }

    /// <summary>
    /// Detaches from the native window. Video output stops but audio may continue.
    /// </summary>
    public void DetachFromWindow()
    {
        if (_disposed) return;
        _mpv_command_string!(_handle, "set wid 0");
        WindowHandle = IntPtr.Zero;
    }

    public void Load(string filePath)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LibMpvEmbeddedTransport));

        if (_isLoaded)
            UnloadInternal();

        string normalizedPath = filePath.Replace("\\", "/");
        string command = $"loadfile \"{normalizedPath}\"";
        int result = _mpv_command_string!(_handle, command);

        if (result < 0)
            throw new InvalidOperationException($"Failed to load file: {filePath} (result: {result})");

        _isLoaded = true;
        _isPaused = true;
        _hasEnded = false;
    }

    public void Play()
    {
        if (!_isLoaded || _disposed)
            throw new ObjectDisposedException(nameof(LibMpvEmbeddedTransport));

        // Wait for media to be ready by polling duration
        for (int i = 0; i < 20; i++)
        {
            if (Duration > 0) break;
            Thread.Sleep(50);
        }

        int result = _mpv_command_string!(_handle, "set pause no");
        if (result < 0)
        {
            result = _mpv_command_string!(_handle, "set_property pause no");
            if (result < 0)
                throw new InvalidOperationException($"Failed to play (result: {result}).");
        }

        _isPaused = false;
    }

    public void Pause()
    {
        if (!_isLoaded || _disposed)
            throw new ObjectDisposedException(nameof(LibMpvEmbeddedTransport));

        int result = _mpv_command_string!(_handle, "set pause yes");
        if (result < 0)
        {
            result = _mpv_command_string!(_handle, "set_property pause yes");
            if (result < 0)
                throw new InvalidOperationException("Failed to set pause state.");
        }

        _isPaused = true;
    }

    public void Seek(long positionMs)
    {
        if (!_isLoaded || _disposed)
            throw new ObjectDisposedException(nameof(LibMpvEmbeddedTransport));

        var dur = Duration;
        if (dur > 0 && positionMs > dur)
            positionMs = dur;

        double positionSec = positionMs / 1000.0;
        string command = $"seek {positionSec:F3} absolute";
        _mpv_command_string!(_handle, command);
        // Don't throw on seek failure — libmpv may reject seeks near end-of-file
    }

    public long CurrentTime
    {
        get
        {
            if (!_isLoaded || _disposed) return 0;
            var str = GetPropertyString("time-pos");
            if (str != null && double.TryParse(str, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double timePos))
                return (long)(timePos * 1000);
            return 0;
        }
    }

    public long Duration
    {
        get
        {
            if (!_isLoaded || _disposed) return 0;
            var str = GetPropertyString("duration");
            if (str != null && double.TryParse(str, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double duration))
                return (long)(duration * 1000);
            return 0;
        }
    }

    public double Volume
    {
        get
        {
            if (_disposed || _handle == IntPtr.Zero) return 1.0;
            var str = GetPropertyString("volume");
            if (str != null && double.TryParse(str, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double vol))
                return Math.Clamp(vol / 100.0, 0.0, 1.0);
            return 1.0;
        }
        set
        {
            if (_disposed || _handle == IntPtr.Zero) return;
            _mpv_command_string!(_handle, $"set volume {Math.Clamp(value, 0.0, 1.0) * 100:F0}");
        }
    }

    public double PlaybackRate
    {
        get
        {
            if (_disposed || _handle == IntPtr.Zero) return 1.0;
            var str = GetPropertyString("speed");
            if (str != null && double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out double spd))
                return spd;
            return 1.0;
        }
        set => _mpv_command_string!(_handle, $"set speed {Math.Clamp(value, 0.1, 4.0):F2}");
    }

    public bool HasEnded
    {
        get
        {
            if (!_isLoaded || _disposed) return false;
            var str = GetPropertyString("eof-reached");
            if (str != null && bool.TryParse(str, out bool eofReached))
                _hasEnded = eofReached;
            return _hasEnded;
        }
    }

    public void LoadSubtitleTrack(string srtPath)
    {
        if (_disposed || _handle == IntPtr.Zero) return;
        var normalized = srtPath.Replace('\\', '/');
        _mpv_command_string!(_handle, $"sub-add \"{normalized}\"");
    }

    public void RemoveAllSubtitleTracks()
    {
        if (_disposed || _handle == IntPtr.Zero) return;
        _mpv_command_string!(_handle, "sub-remove");
    }

    public bool SubtitlesVisible
    {
        get
        {
            if (_disposed || _handle == IntPtr.Zero) return false;
            return GetPropertyString("sub-visibility") != "no";
        }
        set => _mpv_command_string!(_handle, $"set sub-visibility {(value ? "yes" : "no")}");
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            // Signal the event thread to stop.
            _eventThreadRunning = false;

            // Kick mpv_wait_event out of its 0.5-second block by sending a quit
            // command before joining the thread.  Without this the join can stall
            // for up to half a second per iteration — or indefinitely if mpv is
            // mid-render — which is long enough to make the app appear to hang.
            if (_handle != IntPtr.Zero && _mpv_command_string is not null)
            {
                try { _mpv_command_string(_handle, "quit"); }
                catch { /* ignore: we're shutting down anyway */ }
            }

            _eventThread?.Join(TimeSpan.FromSeconds(2));

            if (_handle != IntPtr.Zero)
            {
                _mpv_terminate_destroy!(_handle);
                _handle = IntPtr.Zero;
            }

            // Only free the native DLL handle from managed Dispose, not from the
            // finalizer.  In the finalizer path the OS loader may have already
            // released the module, and calling NativeLibrary.Free on a stale
            // handle can fault the process.
            if (disposing && _dllHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(_dllHandle);
                _dllHandle = IntPtr.Zero;
            }

            _disposed = true;
        }
    }

    ~LibMpvEmbeddedTransport()
    {
        Dispose(false);
    }

    private void UnloadInternal()
    {
        if (_isLoaded && !_isPaused)
            Pause();

        // Remove any previously applied VSR filter before loading a new file.
        if (_options.UseGpuNext && _options.VsrEnabled)
        {
            _mpv_command_string!(_handle, "vf remove @vsr");
            _lastVsrDiagnosticKey = null;
        }

        _mpv_command_string!(_handle, "stop");
        _isLoaded = false;
        _isPaused = true;
        _hasEnded = false;
    }

    // ── VSR event loop ─────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a background thread that polls the mpv event queue and applies the
    /// d3d11vpp VSR filter after MPV_EVENT_FILE_LOADED (event id 8).
    /// </summary>
    private void StartEventThread()
    {
        _eventThreadRunning = true;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-event-vsr" };
        _eventThread.Start();
    }

    private void EventLoop()
    {
        // MPV_EVENT_FILE_LOADED = 8, MPV_EVENT_NONE = 0
        // mpv_wait_event returns a pointer to mpv_event { int event_id; int error; ulong reply; void* data; }
        // The event_id is the first int-sized field of the struct.
        const int MpvEventFileLoaded = 8;

        while (_eventThreadRunning)
        {
            if (_handle == IntPtr.Zero) { Thread.Sleep(50); continue; }

            // Wait up to 0.5 s for the next event; returns a pointer to mpv_event.
            IntPtr eventPtr = _mpv_wait_event!(_handle, 0.5);

            if (!_eventThreadRunning) break;
            if (eventPtr == IntPtr.Zero) continue;

            // Read event_id from the first int-sized field of the mpv_event struct.
            int eventId = Marshal.ReadInt32(eventPtr);
            if (eventId != MpvEventFileLoaded) continue;

            ApplyVsrFilter("file-loaded");
        }
    }

    /// <summary>
    /// Computes the VSR scale factor from video vs. display dimensions and issues
    /// the d3d11vpp filter command. Logs the reason when the request is skipped.
    /// </summary>
    private void ApplyVsrFilter(string trigger)
    {
        if (_disposed || _handle == IntPtr.Zero) return;

        // Query video dimensions.
        var wStr = GetPropertyString("width");
        var hStr = GetPropertyString("height");
        if (!int.TryParse(wStr, out int videoW) || videoW <= 0)
        {
            LogVsrDiagnosticOnce(
                $"skip:{trigger}:video-width-unavailable",
                $"RTX VSR skipped: trigger={trigger}, reason=video-width-unavailable, raw_width='{wStr ?? "<null>"}'");
            return;
        }

        if (!int.TryParse(hStr, out int videoH) || videoH <= 0)
        {
            LogVsrDiagnosticOnce(
                $"skip:{trigger}:video-height-unavailable",
                $"RTX VSR skipped: trigger={trigger}, reason=video-height-unavailable, raw_height='{hStr ?? "<null>"}'");
            return;
        }

        var hwFmt = GetPropertyString("video-params/hw-pixelformat")
                    ?? GetPropertyString("video-params/pixelformat")
                    ?? string.Empty;

        var plan = EvaluateVsrFilterPlan(
            videoW,
            videoH,
            _displayWidth,
            _displayHeight,
            hwFmt,
            _options.VsrQuality);

        if (!plan.ShouldApply || string.IsNullOrWhiteSpace(plan.FilterChain))
        {
            LogVsrDiagnosticOnce(
                $"skip:{trigger}:{plan.Reason}:{plan.VideoWidth}x{plan.VideoHeight}:{plan.DisplayWidth}x{plan.DisplayHeight}:{plan.HwPixelFormat}",
                $"RTX VSR skipped: trigger={trigger}, reason={plan.Reason}, " +
                $"video={plan.VideoWidth}x{plan.VideoHeight}, display={plan.DisplayWidth}x{plan.DisplayHeight}, " +
                $"hwfmt='{plan.HwPixelFormat}', scale={plan.Scale:F1}");
            return;
        }

        // Remove any stale filter first, then add the new one.
        _mpv_command_string!(_handle, "vf remove @vsr");
        int addResult = _mpv_command_string!(_handle, $"vf add {plan.FilterChain}");

        if (addResult < 0)
        {
            _log?.Warning(
                $"RTX VSR filter rejected by libmpv: trigger={trigger}, result={addResult}, " +
                $"filter='{plan.FilterChain}', video={plan.VideoWidth}x{plan.VideoHeight}, " +
                $"display={plan.DisplayWidth}x{plan.DisplayHeight}, hwfmt='{plan.HwPixelFormat}'");
            _lastVsrDiagnosticKey = null;
            return;
        }

        LogVsrDiagnosticOnce(
            $"applied:{trigger}:{plan.FilterChain}",
            $"RTX VSR applied: trigger={trigger}, filter='{plan.FilterChain}', " +
            $"video={plan.VideoWidth}x{plan.VideoHeight}, display={plan.DisplayWidth}x{plan.DisplayHeight}, " +
            $"hwfmt='{plan.HwPixelFormat}', scale={plan.Scale:F1}");
    }

    internal static VsrFilterPlan EvaluateVsrFilterPlan(
        int videoWidth,
        int videoHeight,
        int displayWidth,
        int displayHeight,
        string hwPixelFormat,
        int vsrQuality)
    {
        if (videoWidth <= 0 || videoHeight <= 0)
            return VsrFilterPlan.Skip("video-size-unavailable", videoWidth, videoHeight, displayWidth, displayHeight, hwPixelFormat);

        if (displayWidth <= 0 || displayHeight <= 0)
            return VsrFilterPlan.Skip("display-size-unavailable", videoWidth, videoHeight, displayWidth, displayHeight, hwPixelFormat);

        double scaleExact = Math.Max(displayWidth, displayHeight) / (double)Math.Max(videoWidth, videoHeight);
        double scale = Math.Floor(scaleExact * 10.0) / 10.0;

        if (scale <= 1.0)
            return VsrFilterPlan.Skip("no-upscaling-required", videoWidth, videoHeight, displayWidth, displayHeight, hwPixelFormat, scale);

        bool needsFormatConversion =
            !string.IsNullOrEmpty(hwPixelFormat) &&
            hwPixelFormat != "nv12" &&
            hwPixelFormat != "yuv420p";

        int quality = Math.Clamp(vsrQuality, 1, 4);
        string filterChain = needsFormatConversion
            ? $"@vsr:lavfi=[format=nv12],d3d11vpp:scaling-mode=nvidia:scale={scale:F1}:scaling-quality={quality}"
            : $"@vsr:d3d11vpp:scaling-mode=nvidia:scale={scale:F1}:scaling-quality={quality}";

        return VsrFilterPlan.Apply(filterChain, scale, videoWidth, videoHeight, displayWidth, displayHeight, hwPixelFormat);
    }

    private IntPtr LoadLibMpvDll()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
            string nativeDir = Path.Combine(solutionRoot, "native", "win-x64");

            string[] possibleNames = { "libmpv-2.dll", "libmpv-1.dll", "mpv-2.dll", "mpv-1.dll" };
            foreach (string dllName in possibleNames)
            {
                string path = Path.Combine(nativeDir, dllName);
                if (File.Exists(path))
                {
                    IntPtr handle = NativeLibrary.Load(path);
                    if (handle != IntPtr.Zero) return handle;
                }
            }

            foreach (string dllName in possibleNames)
            {
                string path = Path.Combine(baseDir, dllName);
                if (File.Exists(path))
                {
                    IntPtr handle = NativeLibrary.Load(path);
                    if (handle != IntPtr.Zero) return handle;
                }
            }
        }
        catch
        {
            // Fall through to default loading
        }

        string[] fallbackNames = { "libmpv-2.dll", "libmpv-1.dll", "mpv-2.dll", "mpv-1.dll" };
        foreach (string dllName in fallbackNames)
        {
            IntPtr handle = NativeLibrary.Load(dllName);
            if (handle != IntPtr.Zero) return handle;
        }

        return IntPtr.Zero;
    }

    private void LoadLibMpvFunctions()
    {
        _mpv_create = LoadFunction<mpv_create_delegate>("mpv_create");
        _mpv_initialize = LoadFunction<mpv_initialize_delegate>("mpv_initialize");
        _mpv_set_option_string = LoadFunction<mpv_set_option_string_delegate>("mpv_set_option_string");
        _mpv_command_string = LoadFunction<mpv_command_string_delegate>("mpv_command_string");
        _mpv_get_property_string = LoadFunction<mpv_get_property_string_delegate>("mpv_get_property_string");
        _mpv_free = LoadFunction<mpv_free_delegate>("mpv_free");
        _mpv_terminate_destroy = LoadFunction<mpv_terminate_destroy_delegate>("mpv_terminate_destroy");
        _mpv_wait_event = LoadFunction<mpv_wait_event_delegate>("mpv_wait_event");
    }

    private string? GetPropertyString(string name)
    {
        var ptr = _mpv_get_property_string!(_handle, name);
        if (ptr == IntPtr.Zero) return null;
        var str = Marshal.PtrToStringAnsi(ptr);
        _mpv_free!(ptr);
        return str;
    }

    private T LoadFunction<T>(string functionName) where T : Delegate
    {
        IntPtr funcPtr = NativeLibrary.GetExport(_dllHandle, functionName);
        if (funcPtr == IntPtr.Zero)
            throw new MissingMethodException($"Failed to find libmpv function: {functionName}");
        return Marshal.GetDelegateForFunctionPointer<T>(funcPtr);
    }

    private void SetOption(string name, string value)
    {
        if (_mpv_set_option_string!(_handle, name, value) < 0)
            throw new InvalidOperationException($"Failed to set libmpv option: {name}={value}");
    }

    private void LogVsrDiagnosticOnce(string key, string message)
    {
        if (_lastVsrDiagnosticKey == key)
            return;

        _lastVsrDiagnosticKey = key;
        _log?.Info(message);
    }
}

internal sealed record VsrFilterPlan(
    bool ShouldApply,
    string Reason,
    double Scale,
    string? FilterChain,
    int VideoWidth,
    int VideoHeight,
    int DisplayWidth,
    int DisplayHeight,
    string HwPixelFormat)
{
    public static VsrFilterPlan Skip(
        string reason,
        int videoWidth,
        int videoHeight,
        int displayWidth,
        int displayHeight,
        string hwPixelFormat,
        double scale = 0.0) =>
        new(
            ShouldApply: false,
            Reason: reason,
            Scale: scale,
            FilterChain: null,
            VideoWidth: videoWidth,
            VideoHeight: videoHeight,
            DisplayWidth: displayWidth,
            DisplayHeight: displayHeight,
            HwPixelFormat: hwPixelFormat);

    public static VsrFilterPlan Apply(
        string filterChain,
        double scale,
        int videoWidth,
        int videoHeight,
        int displayWidth,
        int displayHeight,
        string hwPixelFormat) =>
        new(
            ShouldApply: true,
            Reason: "apply",
            Scale: scale,
            FilterChain: filterChain,
            VideoWidth: videoWidth,
            VideoHeight: videoHeight,
            DisplayWidth: displayWidth,
            DisplayHeight: displayHeight,
            HwPixelFormat: hwPixelFormat);
}
