using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace BabelPlayer.Avalonia;

public sealed class MpvNativeHost : NativeControlHost
{
    private IntPtr _childHandle;
    private IntPtr _mpvContext;
    private bool _isInitialized;
    private string? _sourcePath;
    private bool _isPaused = true;

    public event Action<nint>? HostHandleReady;
    public event Action<string>? StatusChanged;
    public event Action<string>? PlaybackFailed;

    public nint HostHandle => _childHandle;

    public bool IsPaused => _isPaused;

    public string? SourcePath
    {
        get => _sourcePath;
        set
        {
            _sourcePath = value;
            if (_isInitialized && !string.IsNullOrWhiteSpace(value))
            {
                Load(value);
            }
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("MpvNativeHost currently supports Windows only.");
        }

        _childHandle = NativeMethods.CreateWindowEx(
            0,
            "static",
            string.Empty,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS,
            0,
            0,
            Math.Max((int)Bounds.Width, 1),
            Math.Max((int)Bounds.Height, 1),
            parent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_childHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the native video host window.");
        }

        InitializeMpv();
        HostHandleReady?.Invoke(_childHandle);
        TryLoadSource();
        return new PlatformHandle(_childHandle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        TearDownMpv();

        if (_childHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_childHandle);
            _childHandle = IntPtr.Zero;
        }

        base.DestroyNativeControlCore(control);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);

        if (_childHandle != IntPtr.Zero)
        {
            NativeMethods.MoveWindow(
                _childHandle,
                0,
                0,
                Math.Max((int)Math.Round(arranged.Width), 1),
                Math.Max((int)Math.Round(arranged.Height), 1),
                true);
        }

        return arranged;
    }

    public void Load(string path)
    {
        if (!_isInitialized)
        {
            _sourcePath = path;
            return;
        }

        if (!File.Exists(path))
        {
            PlaybackFailed?.Invoke($"Test video not found: {path}");
            return;
        }

        try
        {
            _sourcePath = path;
            ExecuteCommand("loadfile", path, "replace");
            _isPaused = false;
            StatusChanged?.Invoke($"Playing {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            PlaybackFailed?.Invoke($"libmpv load failed: {ex.Message}");
        }
    }

    public void Play()
    {
        ExecuteCommand("set", "pause", "no");
        _isPaused = false;
        StatusChanged?.Invoke("Playback resumed.");
    }

    public void Pause()
    {
        ExecuteCommand("set", "pause", "yes");
        _isPaused = true;
        StatusChanged?.Invoke("Playback paused.");
    }

    public void Stop()
    {
        ExecuteCommand("stop");
        _isPaused = true;
        StatusChanged?.Invoke("Playback stopped.");
    }

    public void SeekRelative(TimeSpan delta)
    {
        ExecuteCommand("seek", delta.TotalSeconds.ToString(CultureInfo.InvariantCulture), "relative");
        StatusChanged?.Invoke($"Seeked {delta.TotalSeconds:+0;-0;0}s.");
    }

    public void SetVolume(double volume)
    {
        var percent = Math.Round(Math.Clamp(volume, 0, 1) * 100d, 1);
        ExecuteCommand("set", "volume", percent.ToString(CultureInfo.InvariantCulture));
    }

    private void InitializeMpv()
    {
        if (_isInitialized)
        {
            return;
        }

        _mpvContext = NativeMethods.mpv_create();
        if (_mpvContext == IntPtr.Zero)
        {
            throw new InvalidOperationException("mpv_create returned a null context.");
        }

        VerifyMpvResult(NativeMethods.mpv_set_option_string(_mpvContext, "config", "no"), "mpv_set_option_string(config)");
        VerifyMpvResult(NativeMethods.mpv_set_option_string(_mpvContext, "terminal", "no"), "mpv_set_option_string(terminal)");
        VerifyMpvResult(NativeMethods.mpv_set_option_string(_mpvContext, "idle", "yes"), "mpv_set_option_string(idle)");
        VerifyMpvResult(NativeMethods.mpv_set_option_string(_mpvContext, "force-window", "yes"), "mpv_set_option_string(force-window)");
        VerifyMpvResult(NativeMethods.mpv_set_option_string(_mpvContext, "keep-open", "yes"), "mpv_set_option_string(keep-open)");
        VerifyMpvResult(NativeMethods.mpv_set_option_string(_mpvContext, "input-default-bindings", "no"), "mpv_set_option_string(input-default-bindings)");
        VerifyMpvResult(NativeMethods.mpv_set_option_string(_mpvContext, "input-vo-keyboard", "no"), "mpv_set_option_string(input-vo-keyboard)");
        VerifyMpvResult(NativeMethods.mpv_set_option_string(_mpvContext, "osc", "no"), "mpv_set_option_string(osc)");
        VerifyMpvResult(NativeMethods.mpv_set_option_string(_mpvContext, "wid", _childHandle.ToString(CultureInfo.InvariantCulture)), "mpv_set_option_string(wid)");
        VerifyMpvResult(NativeMethods.mpv_initialize(_mpvContext), "mpv_initialize");

        _isInitialized = true;
        StatusChanged?.Invoke("Playback surface ready.");
    }

    private void TearDownMpv()
    {
        if (_mpvContext != IntPtr.Zero)
        {
            NativeMethods.mpv_terminate_destroy(_mpvContext);
            _mpvContext = IntPtr.Zero;
            _isInitialized = false;
            _isPaused = true;
        }
    }

    private void TryLoadSource()
    {
        if (_isInitialized && !string.IsNullOrWhiteSpace(_sourcePath))
        {
            Load(_sourcePath);
        }
    }

    private void ExecuteCommand(params string[] args)
    {
        if (!_isInitialized || _mpvContext == IntPtr.Zero)
        {
            throw new InvalidOperationException("libmpv is not initialized.");
        }

        VerifyMpvResult(NativeMethods.mpv_command(_mpvContext, args), $"mpv_command({string.Join(", ", args)})");
    }

    private static void VerifyMpvResult(int result, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"{operation} failed with mpv error code {result}.");
        }
    }

    private static class NativeMethods
    {
        private const string User32 = "user32.dll";
        private const string MpvLibrary = "libmpv-2.dll";

        internal const int WS_CHILD = 0x40000000;
        internal const int WS_VISIBLE = 0x10000000;
        internal const int WS_CLIPSIBLINGS = 0x04000000;

        [DllImport(User32, EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(
            int exStyle,
            string className,
            string windowName,
            int style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parentHandle,
            IntPtr menuHandle,
            IntPtr instanceHandle,
            IntPtr parameter);

        [DllImport(User32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport(User32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool repaint);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mpv_create();

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mpv_initialize(IntPtr ctx);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mpv_set_option_string(
            IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void mpv_terminate_destroy(IntPtr ctx);

        [DllImport(MpvLibrary, EntryPoint = "mpv_command", CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_command_raw(IntPtr ctx, IntPtr args);

        internal static int mpv_command(IntPtr ctx, string[] args)
        {
            var argPointers = new IntPtr[args.Length + 1];
            IntPtr buffer = IntPtr.Zero;

            try
            {
                for (var index = 0; index < args.Length; index++)
                {
                    argPointers[index] = Marshal.StringToCoTaskMemUTF8(args[index]);
                }

                buffer = Marshal.AllocCoTaskMem(IntPtr.Size * argPointers.Length);
                for (var index = 0; index < argPointers.Length; index++)
                {
                    Marshal.WriteIntPtr(buffer, index * IntPtr.Size, argPointers[index]);
                }

                return mpv_command_raw(ctx, buffer);
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(buffer);
                }

                foreach (var pointer in argPointers)
                {
                    if (pointer != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(pointer);
                    }
                }
            }
        }
    }
}
