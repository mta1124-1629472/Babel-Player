using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace BabelPlayer.Avalonia;

public sealed class MpvNativeHost : NativeControlHost
{
    private NativeMethods.WndProc? _subclassProc;
    private IntPtr _childHandle;
    private IntPtr _previousWndProc;

    public event Action<nint>? HostHandleReady;
    public event Action? MouseActivity;
    public event Action? HostDoubleClicked;

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

        _subclassProc = ChildWindowProc;
        _previousWndProc = NativeMethods.SetWindowLongPtr(_childHandle, NativeMethods.GWL_WNDPROC, _subclassProc);
        HostHandleReady?.Invoke(_childHandle);
        return new PlatformHandle(_childHandle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_childHandle != IntPtr.Zero)
        {
            if (_previousWndProc != IntPtr.Zero)
            {
                NativeMethods.SetWindowLongPtr(_childHandle, NativeMethods.GWL_WNDPROC, _previousWndProc);
                _previousWndProc = IntPtr.Zero;
            }

            NativeMethods.DestroyWindow(_childHandle);
            _childHandle = IntPtr.Zero;
        }

        _subclassProc = null;
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

    private IntPtr ChildWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_MOUSEMOVE:
                MouseActivity?.Invoke();
                break;

            case NativeMethods.WM_LBUTTONDBLCLK:
                MouseActivity?.Invoke();
                HostDoubleClicked?.Invoke();
                break;
        }

        return _previousWndProc != IntPtr.Zero
            ? NativeMethods.CallWindowProc(_previousWndProc, hwnd, message, wParam, lParam)
            : NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static class NativeMethods
    {
        private const string User32 = "user32.dll";

        internal const int GWL_WNDPROC = -4;
        internal const int WS_CHILD = 0x40000000;
        internal const int WS_VISIBLE = 0x10000000;
        internal const int WS_CLIPSIBLINGS = 0x04000000;
        internal const uint WM_MOUSEMOVE = 0x0200;
        internal const uint WM_LBUTTONDBLCLK = 0x0203;

        internal delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

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

        [DllImport(User32, EntryPoint = "CallWindowProcW", SetLastError = true)]
        internal static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport(User32, EntryPoint = "DefWindowProcW", SetLastError = true)]
        internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProc newProc)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, Marshal.GetFunctionPointerForDelegate(newProc))
                : new IntPtr(SetWindowLong32(hWnd, nIndex, Marshal.GetFunctionPointerForDelegate(newProc).ToInt32()));
        }

        internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newProc)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, newProc)
                : new IntPtr(SetWindowLong32(hWnd, nIndex, newProc.ToInt32()));
        }

        [DllImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport(User32, EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
