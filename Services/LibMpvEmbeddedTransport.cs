using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Babel.Player.Services;

/// <summary>
/// Embedded media transport using libmpv with GPU-accelerated rendering into a native window.
/// Unlike LibMpvHeadlessTransport, this renders video into an HWND provided via the --wid option,
/// using libmpv's own GPU pipeline (OpenGL/D3D11 under the hood).
/// </summary>
public class LibMpvEmbeddedTransport : IMediaTransport, IDisposable
{
    private IntPtr _handle = IntPtr.Zero;
    private bool _disposed;
    private bool _isLoaded;
    private bool _isPaused;
    private bool _hasEnded;

    private delegate IntPtr mpv_create_delegate();
    private delegate int mpv_initialize_delegate(IntPtr handle);
    private delegate int mpv_set_option_string_delegate(IntPtr handle, string name, string value);
    private delegate int mpv_command_string_delegate(IntPtr handle, string command);
    private delegate IntPtr mpv_get_property_string_delegate(IntPtr handle, string name);
    private delegate void mpv_free_delegate(IntPtr data);
    private delegate void mpv_terminate_destroy_delegate(IntPtr handle);

    private mpv_create_delegate? _mpv_create;
    private mpv_initialize_delegate? _mpv_initialize;
    private mpv_set_option_string_delegate? _mpv_set_option_string;
    private mpv_command_string_delegate? _mpv_command_string;
    private mpv_get_property_string_delegate? _mpv_get_property_string;
    private mpv_free_delegate? _mpv_free;
    private mpv_terminate_destroy_delegate? _mpv_terminate_destroy;

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

    public LibMpvEmbeddedTransport()
    {
        _dllHandle = LoadLibMpvDll();
        if (_dllHandle == IntPtr.Zero)
            throw new DllNotFoundException("libmpv DLL not found.");

        LoadLibMpvFunctions();

        _handle = _mpv_create!();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create libmpv context.");

        // Use gpu-accelerated video output (renders into wid if set)
        SetOption("vo", "gpu");
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
            System.Threading.Thread.Sleep(50);
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
            if (str != null && double.TryParse(str, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double timePos))
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
            if (str != null && double.TryParse(str, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double duration))
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
            if (str != null && double.TryParse(str, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double vol))
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

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (_handle != IntPtr.Zero)
            {
                _mpv_terminate_destroy!(_handle);
                _handle = IntPtr.Zero;
            }

            if (_dllHandle != IntPtr.Zero)
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

        _mpv_command_string!(_handle, "stop");
        _isLoaded = false;
        _isPaused = true;
        _hasEnded = false;
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
}
