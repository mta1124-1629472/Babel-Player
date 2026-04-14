using System.Runtime.InteropServices;

namespace Babel.Player.Services;

/// <summary>
/// Subfolders under <c>native/</c> and <c>tools/</c> for Windows unpacked layouts (matches .NET RIDs: win-x64, win-arm64).
/// </summary>
internal static class WindowsPackagingPaths
{
    /// <summary> e.g. <c>win-arm64</c> when this process is ARM64 on Windows. </summary>
    internal static string NativeRidFolder { get; } = ResolveNativeRidFolder();

    private static string ResolveNativeRidFolder()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "win-x64";

        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };
    }
}
