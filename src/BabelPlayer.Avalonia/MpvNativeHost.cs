using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace BabelPlayer.Avalonia;

public sealed class MpvNativeHost : NativeControlHost
{
    private IntPtr _childHandle;

    public event Action<nint>? HostHandleReady;

    public nint HostHandle => _childHandle;

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

        HostHandleReady?.Invoke(_childHandle);
        return new PlatformHandle(_childHandle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
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

    private static class NativeMethods
    {
        private const string User32 = "user32.dll";

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
    }
}
