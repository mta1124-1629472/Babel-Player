using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Babel.Deck.Views;

/// <summary>
/// Avalonia control that hosts a native Win32 child window for libmpv video rendering.
/// Uses NativeControlHost to embed a native HWND that libmpv renders into via its --wid option.
/// </summary>
public partial class MpvVideoView : NativeControlHost
{
    private IntPtr _childHwnd = IntPtr.Zero;

    /// <summary>
    /// The native window handle that libmpv should render into.
    /// Available after the control is attached to the visual tree.
    /// </summary>
    public IntPtr NativeHandle => _childHwnd;

    /// <summary>
    /// Fired when the native window handle becomes available.
    /// </summary>
    public event EventHandler<IntPtr>? HandleReady;

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
}

/// <summary>
/// Simple platform handle wrapper.
/// </summary>
internal sealed class PlatformHandle(IntPtr handle, string descriptor) : IPlatformHandle
{
    public IntPtr Handle { get; } = handle;
    public string HandleDescriptor { get; } = descriptor;
}
