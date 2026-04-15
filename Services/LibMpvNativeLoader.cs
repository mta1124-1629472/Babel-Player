using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Babel.Player.Services;

/// <summary>
/// Resolves and loads libmpv on Windows without relying on a fixed number of ".." segments
/// from <see cref="AppContext.BaseDirectory"/> (publish layouts differ).
/// Uses <c>LoadLibraryEx</c> with <c>LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR</c> so dependent DLL search
/// includes the directory that contains <c>libmpv-2.dll</c>.
/// </summary>
internal static class LibMpvNativeLoader
{
    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x0000_1000;
    private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x0000_0100;

    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    private static readonly string[] s_preferredNames =
    {
        "libmpv-2.dll", "libmpv-1.dll", "mpv-2.dll", "mpv-1.dll",
    };

    internal static IntPtr Load()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateCandidatePaths())
        {
            var full = Path.GetFullPath(path);
            if (!seen.Add(full) || !File.Exists(full))
                continue;

            var handle = LoadLibraryExW(
                full,
                IntPtr.Zero,
                LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
            if (handle != IntPtr.Zero)
                return handle;

            try
            {
                return NativeLibrary.Load(full);
            }
            catch (DllNotFoundException)
            {
                // Try next candidate path.
            }
        }

        foreach (var name in s_preferredNames)
        {
            try
            {
                return NativeLibrary.Load(name);
            }
            catch (DllNotFoundException)
            {
                // Last-resort name-only load (PATH / process directory).
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> EnumerateCandidatePaths()
    {
        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir))
            yield break;

        // Walk ancestors (bin/Debug/net10.0 → repo root, or publish folder parents).
        var depth = 0;
        var rid = WindowsPackagingPaths.NativeRidFolder;
        for (var dir = baseDir; !string.IsNullOrEmpty(dir) && depth < 14; depth++)
        {
            foreach (var name in s_preferredNames)
                yield return Path.Combine(dir, "native", rid, name);

            var parent = Directory.GetParent(dir);
            dir = parent?.FullName ?? string.Empty;
        }

        foreach (var name in s_preferredNames)
        {
            yield return Path.Combine(baseDir, name);
            yield return Path.Combine(baseDir, "native", rid, name);
        }
    }
}
