using System.Runtime.InteropServices;

namespace BabelPlayer.Playback.Mpv;

internal enum MpvFormat
{
    None = 0,
    String = 1,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    EndFile = 7,
    FileLoaded = 8,
    PropertyChange = 22
}

internal enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5
}

[StructLayout(LayoutKind.Explicit)]
internal struct MpvNode
{
    [FieldOffset(0)]
    public MpvNodeUnion Value;

    [FieldOffset(8)]
    public MpvFormat Format;
}

[StructLayout(LayoutKind.Explicit)]
internal struct MpvNodeUnion
{
    [FieldOffset(0)]
    public IntPtr String;

    [FieldOffset(0)]
    public int Flag;

    [FieldOffset(0)]
    public long Int64;

    [FieldOffset(0)]
    public double Double;

    [FieldOffset(0)]
    public IntPtr List;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvNodeList
{
    public int Num;
    private readonly int _padding;
    public IntPtr Values;
    public IntPtr Keys;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserData;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    private readonly int _padding;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

internal static class LibMpvNative
{
    // On Windows the bundled DLL is libmpv-2.dll.
    // On Linux the system package provides libmpv.so.2.
    // Using "libmpv" (no extension) lets the .NET native library resolver
    // apply the correct OS-specific prefix/suffix automatically.
    private const string MpvLibrary = "libmpv";

    [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_create();

    [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_initialize(IntPtr ctx);

    [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free(IntPtr data);

    [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free_node_contents(ref MpvNode node);

    [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_observe_property(
        IntPtr ctx,
        ulong replyUserdata,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format);

    [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_option_string(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(MpvLibrary, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property_double(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        out double data);

    [DllImport(MpvLibrary, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property_flag(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        out int data);

    [DllImport(MpvLibrary, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property_int64(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        out long data);

    [DllImport(MpvLibrary, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property_string(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        out IntPtr data);

    [DllImport(MpvLibrary, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property_node(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        out MpvNode data);

    [DllImport(MpvLibrary, EntryPoint = "mpv_command", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_command_raw(IntPtr ctx, IntPtr args);

    internal static int mpv_command(IntPtr ctx, params string[] args)
    {
        var stringPointers = new IntPtr[args.Length + 1];
        try
        {
            for (var index = 0; index < args.Length; index++)
            {
                stringPointers[index] = Marshal.StringToCoTaskMemUTF8(args[index]);
            }

            var buffer = Marshal.AllocCoTaskMem(IntPtr.Size * stringPointers.Length);
            try
            {
                for (var index = 0; index < stringPointers.Length; index++)
                {
                    Marshal.WriteIntPtr(buffer, index * IntPtr.Size, stringPointers[index]);
                }

                return mpv_command_raw(ctx, buffer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
        finally
        {
            foreach (var pointer in stringPointers.Where(pointer => pointer != IntPtr.Zero))
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }
}
