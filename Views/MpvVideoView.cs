using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Babel.Player.Views;

/// <summary>
/// Avalonia control that hosts a native Win32 child window for libmpv video rendering.
/// Uses NativeControlHost to embed a native HWND that libmpv renders into via its --wid option.
/// </summary>
public partial class MpvVideoView : NativeControlHost
{
    private const int GwlWndProc = -4;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMouseWheel = 0x020A;

    private IntPtr _childHwnd = IntPtr.Zero;
    private IntPtr _previousWndProc = IntPtr.Zero;
    private WndProcDelegate? _wndProcDelegate;

    /// <summary>
    /// The native window handle that libmpv should render into.
    /// Available after the control is attached to the visual tree.
    /// </summary>
    public IntPtr NativeHandle => _childHwnd;

    /// <summary>
    /// Fired when the native window handle becomes available.
    /// </summary>
    public event EventHandler<IntPtr>? HandleReady;

    /// <summary>
    /// Fired when the native child HWND reports pointer activity.
    /// Used by fullscreen chrome auto-hide logic to recover controls while the mouse
    /// is over the native video surface.
    /// </summary>
    public event EventHandler? NativePointerActivity;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _childHwnd = CreateWindowExW(
                0x00000000,     // dwExStyle: 0
                "STATIC",       // lpClassName
                "",             // lpWindowName
                0x40000000 | 0x10000000 | 0x04000000, // WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS
                0, 0,
                (int)Bounds.Width,
                (int)Bounds.Height,
                parent.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_childHwnd != IntPtr.Zero)
            {
                HookNativeWndProc();
                HandleReady?.Invoke(this, _childHwnd);
                return new PlatformHandle(_childHwnd, "HWND");
            }
        }

        // Fallback: let the base create a default control
        return base.CreateNativeControlCore(parent);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_childHwnd != IntPtr.Zero && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            UnhookNativeWndProc();
            DestroyWindow(_childHwnd);
            _childHwnd = IntPtr.Zero;
        }

        base.DestroyNativeControlCore(control);
    }

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void HookNativeWndProc()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || _childHwnd == IntPtr.Zero || _previousWndProc != IntPtr.Zero)
            return;

        _wndProcDelegate = NativeWndProc;
        var newProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _previousWndProc = SetWindowLongPtr(_childHwnd, GwlWndProc, newProcPtr);
    }

    private void UnhookNativeWndProc()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || _childHwnd == IntPtr.Zero || _previousWndProc == IntPtr.Zero)
            return;

        _ = SetWindowLongPtr(_childHwnd, GwlWndProc, _previousWndProc);
        _previousWndProc = IntPtr.Zero;
        _wndProcDelegate = null;
    }

    private IntPtr NativeWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg is WmMouseMove or WmLButtonDown or WmRButtonDown or WmMButtonDown or WmMouseWheel)
            NativePointerActivity?.Invoke(this, EventArgs.Empty);

        if (_previousWndProc != IntPtr.Zero)
            return CallWindowProc(_previousWndProc, hWnd, msg, wParam, lParam);

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }
}

/// <summary>
/// Simple platform handle wrapper.
/// </summary>
internal sealed class PlatformHandle(IntPtr handle, string descriptor) : IPlatformHandle
{
    public IntPtr Handle { get; } = handle;
    public string HandleDescriptor { get; } = descriptor;
}
