using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace BabelPlayer.Avalonia;

public sealed class MpvNativeHost : NativeControlHost
{
    private static readonly string TestVideoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "test-video.mp4");
    private IntPtr _childHandle;
    private IntPtr _mpvContext;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("MpvNativeHost proof-of-concept currently supports Windows only.");
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
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the native mpv host window.");
        }

        InitializeMpv(_childHandle);
        return new PlatformHandle(_childHandle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_mpvContext != IntPtr.Zero)
        {
            NativeMethods.mpv_terminate_destroy(_mpvContext);
            _mpvContext = IntPtr.Zero;
        }

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

    private void InitializeMpv(IntPtr hostHandle)
    {
        if (!File.Exists(TestVideoPath))
        {
            throw new FileNotFoundException($"The proof-of-concept test video was not found at '{TestVideoPath}'.", TestVideoPath);
        }

        _mpvContext = NativeMethods.mpv_create();
        if (_mpvContext == IntPtr.Zero)
        {
            throw new InvalidOperationException("mpv_create returned a null context.");
        }

        VerifyMpvResult(NativeMethods.mpv_set_option_string(_mpvContext, "wid", hostHandle.ToInt64().ToString()), "mpv_set_option_string(wid)");
        VerifyMpvResult(NativeMethods.mpv_initialize(_mpvContext), "mpv_initialize");
        VerifyMpvResult(
            NativeMethods.mpv_command(_mpvContext, new[] { "loadfile", TestVideoPath, "replace", null! }),
            "mpv_command(loadfile)");
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
        private const string MpvLibrary = "libmpv-2.dll";
        private const string User32 = "user32.dll";

        internal const int WS_CHILD = 0x40000000;
        internal const int WS_VISIBLE = 0x10000000;
        internal const int WS_CLIPSIBLINGS = 0x04000000;

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mpv_create();

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mpv_initialize(IntPtr ctx);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int mpv_set_option_string(IntPtr ctx, string name, string value);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int mpv_command(
            IntPtr ctx,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] args);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void mpv_terminate_destroy(IntPtr ctx);

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
    }
}
