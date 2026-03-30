using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Babel.Player.Services;

/// <summary>
/// Headless media transport implementation using libmpv.
/// This is a minimal implementation for Milestone 2 proof of concept.
/// </summary>
public class LibMpvHeadlessTransport : IMediaTransport, IDisposable
{
    // libmpv handle
    private IntPtr _handle = IntPtr.Zero;
    
    // Track if we've been disposed
    private bool _disposed;
    
    // Current state
    private bool _isLoaded;
    private bool _isPaused;
    private bool _hasEnded;
    
    // Libmpv function pointers
    private delegate IntPtr mpv_create_delegate();
    private delegate int mpv_initialize_delegate(IntPtr handle);
    private delegate int mpv_set_option_string_delegate(IntPtr handle, string name, string value);
    private delegate int mpv_command_string_delegate(IntPtr handle, string command);
    private delegate IntPtr mpv_get_property_string_delegate(IntPtr handle, string name);
    private delegate void mpv_free_delegate(IntPtr data);
    private delegate void mpv_terminate_destroy_delegate(IntPtr handle);
    // Simplified wait event delegate without the output parameter we're not using
    private delegate int mpv_wait_event_delegate(IntPtr handle, double timeout);

    // Libmpv function instances (initialized by LoadLibMpvFunctions in constructor)
    private mpv_create_delegate? _mpv_create;
    private mpv_initialize_delegate? _mpv_initialize;
    private mpv_set_option_string_delegate? _mpv_set_option_string;
    private mpv_command_string_delegate? _mpv_command_string;
    private mpv_get_property_string_delegate? _mpv_get_property_string;
    private mpv_free_delegate? _mpv_free;
    private mpv_terminate_destroy_delegate? _mpv_terminate_destroy;
    private mpv_wait_event_delegate? _mpv_wait_event;
    
    // DLL handle
    private IntPtr _dllHandle = IntPtr.Zero;
    
    // Event for ended
#pragma warning disable CS0067 // Events are never used in this headless implementation
    public event EventHandler? Ended;
    public event EventHandler<Exception>? ErrorOccurred;
#pragma warning restore CS0067
    
    public LibMpvHeadlessTransport(bool suppressAudio = true)
    {
        // Try to load libmpv DLL
        _dllHandle = LoadLibMpvDll();
        if (_dllHandle == IntPtr.Zero)
        {
            throw new DllNotFoundException("libmpv DLL not found. Please ensure libmpv is installed and accessible.");
        }

        // Load function pointers
        LoadLibMpvFunctions();

        // Create libmpv context
        _handle = _mpv_create!();
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create libmpv context.");
        }

        // Set options for headless operation
        SetOption("vo", "null");   // Null video output
        if (suppressAudio)
            SetOption("ao", "null");   // Null audio output (headless / test mode)
        SetOption("idle", "yes");  // Wait for commands instead of exiting
        
        // Initialize libmpv
        if (_mpv_initialize!(_handle) != 0)
        {
            throw new InvalidOperationException("Failed to initialize libmpv.");
        }
        
        _isLoaded = false;
        _isPaused = true;
        _hasEnded = false;
    }
    
    private IntPtr LoadLibMpvDll()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            // Go up three levels to get to the solution root from the bin directory
            string solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
            string nativeDir = Path.Combine(solutionRoot, "native", "win-x64");
            
            // Try the native directory first
            string[] possibleNames = { "libmpv-2.dll", "libmpv-1.dll", "mpv-2.dll", "mpv-1.dll" };
            foreach (string dllName in possibleNames)
            {
                string path = Path.Combine(nativeDir, dllName);
                if (File.Exists(path))
                {
                    IntPtr handle = NativeLibrary.Load(path);
                    if (handle != IntPtr.Zero)
                    {
                        return handle;
                    }
                }
            }
            
            // If not found in native directory, try the base directory (where the exe is)
            foreach (string dllName in possibleNames)
            {
                string path = Path.Combine(baseDir, dllName);
                if (File.Exists(path))
                {
                    IntPtr handle = NativeLibrary.Load(path);
                    if (handle != IntPtr.Zero)
                    {
                        return handle;
                    }
                }
            }
        }
        catch
        {
            // If we fail, we'll fall back to default loading below
            // For debugging, we could log the exception, but we'll just ignore and fall back.
        }
        
        // Fall back to default loading (from system paths or same directory as exe)
        string[] fallbackNames = { "libmpv-2.dll", "libmpv-1.dll", "mpv-2.dll", "mpv-1.dll" };
        foreach (string dllName in fallbackNames)
        {
            IntPtr handle = NativeLibrary.Load(dllName);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }
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
        {
            throw new MissingMethodException($"Failed to find libmpv function: {functionName}");
        }
        return Marshal.GetDelegateForFunctionPointer<T>(funcPtr);
    }
    
    private void SetOption(string name, string value)
    {
        if (_mpv_set_option_string!(_handle, name, value) < 0)
        {
            throw new InvalidOperationException($"Failed to set libmpv option: {name}={value}");
        }
    }
    
    public void Load(string filePath)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LibMpvHeadlessTransport));
        }
        
        // Unload any existing file
        if (_isLoaded)
        {
            UnloadInternal();
        }
        
        // Convert Windows backslashes to forward slashes for libmpv
        string normalizedPath = filePath.Replace("\\", "/");
        string command = $"loadfile \"{normalizedPath}\"";
        int result = _mpv_command_string!(_handle, command);
        
        if (result < 0)
        {
            throw new InvalidOperationException($"Failed to load file: {filePath} (result: {result})");
        }
        
        _isLoaded = true;
        _isPaused = true;
        _hasEnded = false;
        
        // Note: Don't auto-play here. Let the caller explicitly call Play() if needed.
        // This avoids race condition where set_property fails because file isn't ready.
    }
    
    public void Play()
    {
        if (!_isLoaded || _disposed)
        {
            throw new ObjectDisposedException(nameof(LibMpvHeadlessTransport));
        }
        
        // Wait for media to be ready by polling duration
        for (int i = 0; i < 20; i++)
        {
            if (Duration > 0) break;
            System.Threading.Thread.Sleep(50);
        }
        
        // Now try to set pause to no
        int result = _mpv_command_string!(_handle, "set pause no");
        
        if (result < 0)
        {
            // Try alternative syntax
            result = _mpv_command_string!(_handle, "set_property pause no");
            if (result < 0)
            {
                throw new InvalidOperationException($"Failed to play (result: {result}).");
            }
        }
        
        _isPaused = false;
    }
    
    public void Pause()
    {
        if (!_isLoaded || _disposed)
        {
            throw new ObjectDisposedException(nameof(LibMpvHeadlessTransport));
        }
        
        int result = _mpv_command_string!(_handle, "set pause yes");
        if (result < 0)
        {
            result = _mpv_command_string!(_handle, "set_property pause yes");
            if (result < 0)
            {
                throw new InvalidOperationException("Failed to set pause state.");
            }
        }
        
        _isPaused = true;
    }
    
    public void Seek(long positionMs)
    {
        if (!_isLoaded || _disposed)
        {
            throw new ObjectDisposedException(nameof(LibMpvHeadlessTransport));
        }
        
        string command = $"seek {positionMs} absolute";
        if (_mpv_command_string!(_handle, command) < 0)
        {
            throw new InvalidOperationException($"Failed to seek to {positionMs}ms.");
        }
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
    
    private double _volume = 1.0;
    public double Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0.0, 1.0);
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
    
    private void UnloadInternal()
    {
        // Stop playback
        if (_isLoaded && !_isPaused)
        {
            Pause();
        }
        
        // Unload the file
        _mpv_command_string!(_handle, "stop");
        _isLoaded = false;
        _isPaused = true;
        _hasEnded = false;
    }
    
    ~LibMpvHeadlessTransport()
    {
        Dispose(false);
    }
}