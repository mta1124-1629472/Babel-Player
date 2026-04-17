using System;
using System.Collections.Generic;
using System.IO;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

[Collection("Environment")]
public sealed class DependencyLocatorSmokeTests : IDisposable
{
    private readonly List<string> _createdPaths = [];
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        // DependencyLocator caches positive probe results for the session; drop
        // any entries this test installed so later tests in the same process
        // don't resolve our shim paths.
        DependencyLocator.ClearProbeCache();

        foreach (var path in _createdPaths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup for test-created shims.
            }
        }
    }

    // Note: these tests assert that `FindFfmpeg/Ffprobe` resolve *into* the
    // bundled `tools/{rid}/` directory, not that they hit the explicit
    // `ffmpeg.exe` entries in the candidate list. On Windows we write a `.cmd`
    // shim (a bare `.exe` placeholder can't execute and would fail Probe's
    // `-version` check), so resolution actually succeeds via the bare
    // `"ffmpeg"` candidate + PATH lookup (PATHEXT picks up `.cmd`). Because we
    // prepend `tools/{rid}/` to PATH, the resolved path still starts with that
    // directory — which is the invariant the app relies on at runtime.

    [Fact]
    [Trait("Category", "Smoke")]
    public void FindFfmpeg_ResolvesIntoBundledToolsRidDirectory()
    {
        var toolDir = EnsureBundledToolDirectory();
        EnsureBundledToolCommand(toolDir, "ffmpeg");

        var resolvedPath = DependencyLocator.FindFfmpeg();

        Assert.NotNull(resolvedPath);
        Assert.StartsWith(
            Path.GetFullPath(toolDir) + Path.DirectorySeparatorChar,
            Path.GetFullPath(resolvedPath!),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void FindFfprobe_ResolvesIntoBundledToolsRidDirectory()
    {
        var toolDir = EnsureBundledToolDirectory();
        EnsureBundledToolCommand(toolDir, "ffprobe");

        var resolvedPath = DependencyLocator.FindFfprobe();

        Assert.NotNull(resolvedPath);
        Assert.StartsWith(
            Path.GetFullPath(toolDir) + Path.DirectorySeparatorChar,
            Path.GetFullPath(resolvedPath!),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureBundledToolDirectory()
    {
        var toolDir = Path.Combine(AppContext.BaseDirectory, "tools", WindowsPackagingPaths.NativeRidFolder);
        Directory.CreateDirectory(toolDir);
        Environment.SetEnvironmentVariable(
            "PATH",
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PATH"))
                ? toolDir
                : toolDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        return toolDir;
    }

    private void EnsureBundledToolCommand(string toolDir, string toolName)
    {
        var commandPath = OperatingSystem.IsWindows()
            ? Path.Combine(toolDir, $"{toolName}.cmd")
            : Path.Combine(toolDir, toolName);

        if (!File.Exists(commandPath))
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(
                    commandPath,
                    "@echo off\r\n" +
                    "if /I \"%~1\"==\"-version\" exit /b 0\r\n" +
                    "exit /b 0\r\n");
            }
            else
            {
                File.WriteAllText(
                    commandPath,
                    "#!/usr/bin/env sh\n" +
                    "if [ \"$1\" = \"-version\" ]; then\n" +
                    "  exit 0\n" +
                    "fi\n" +
                    "exit 0\n");

                File.SetUnixFileMode(
                    commandPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        _createdPaths.Add(commandPath);
    }
}
