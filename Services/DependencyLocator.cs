using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Babel.Player.Models;

namespace Babel.Player.Services;

/// <summary>
/// Probes the local environment for required external tools (Python, ffmpeg).
/// Returns the working executable path, or null if the tool cannot be found.
/// </summary>
public static class DependencyLocator
{
    private const int ProbeTimeoutMs = 500;

    /// <summary>Returns a working Python executable path, or null if not found.</summary>
    public static string? FindPython()
    {
        var appDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, "python.exe"),
            Path.Combine(appDir, "python", "python.exe"),
            "python",
            "python3",
        };
        return Probe(candidates, "--version");
    }

    /// <summary>Returns a working ffmpeg executable path, or null if not found.</summary>
    public static string? FindFfmpeg()
    {
        var appDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, "ffmpeg.exe"),
            Path.Combine(appDir, "tools", "ffmpeg.exe"),
            "ffmpeg",
        };
        return Probe(candidates, "-version");
    }

    /// <summary>Returns a working piper executable path, or null if not found.</summary>
    public static string? FindPiper()
    {
        var appDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, $"{ProviderNames.Piper}.exe"),
            Path.Combine(appDir, ProviderNames.Piper, $"{ProviderNames.Piper}.exe"),
            ProviderNames.Piper,
        };
        return Probe(candidates, "--version");
    }

    private static string? Probe(string[] candidates, string versionArg)
    {
        foreach (var path in candidates)
        {
            var resolved = ResolveExecutable(path);
            if (resolved is null)
                continue;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = resolved,
                    Arguments = versionArg,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    if (proc.WaitForExit(ProbeTimeoutMs) && proc.ExitCode == 0)
                        return resolved;
                    else
                        try { proc.Kill(); } catch { /* best-effort cleanup */ }
                }
            }
            catch
            {
                // Not found at this path — continue probing
            }
        }
        return null;
    }

    private static string? ResolveExecutable(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        // Absolute or relative explicit path
        if (candidate.Contains(Path.DirectorySeparatorChar) ||
            candidate.Contains(Path.AltDirectorySeparatorChar) ||
            Path.IsPathRooted(candidate))
        {
            return File.Exists(candidate) ? candidate : null;
        }

        // Command name: resolve against PATH (and PATHEXT on Windows) before spawning.
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        var extensions = GetExecutableExtensions();
        var dirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in dirs)
        {
            var trimmedDir = dir.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(trimmedDir))
                continue;

            foreach (var ext in extensions)
            {
                var full = Path.Combine(trimmedDir, candidate + ext);
                if (File.Exists(full))
                    return full;
            }
        }

        return null;
    }

    private static string[] GetExecutableExtensions()
    {
        if (!OperatingSystem.IsWindows())
            return [string.Empty];

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathext))
            return [".exe", ".cmd", ".bat", string.Empty];

        var parsed = pathext
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(ext => ext.Trim())
            .Where(ext => !string.IsNullOrWhiteSpace(ext))
            .ToList();

        if (!parsed.Contains(string.Empty))
            parsed.Add(string.Empty);

        return [.. parsed];
    }
}
