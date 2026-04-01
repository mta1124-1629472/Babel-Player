using System;
using System.Diagnostics;
using System.IO;
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
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
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
                        return path;
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
}
