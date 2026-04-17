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
        try
        {
            Environment.SetEnvironmentVariable("PATH", _originalPath);

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
        finally
        {
            DependencyLocator.ClearProbeCache();
        }
    }

    // Note: these tests assert that `FindFfmpeg/Ffprobe` resolve *into* the
    // bundled `tools/{rid}/` directory. On Windows we write a `.cmd` shim (a
    // bare `.exe` placeholder can't execute and would fail Probe's `-version`
    // check); the candidate list in `DependencyLocator` includes `.cmd` and
    // `.bat` entries alongside `.exe`, so the explicit `tools/{rid}/` entry
    // matches. We also prepend `tools/{rid}/` to PATH as a belt-and-braces
    // fallback so the invariant (resolved path lives inside `tools/{rid}/`)
    // holds whichever branch wins.

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

        var created = false;
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

            created = true;
        }

        if (created)
            _createdPaths.Add(commandPath);
    }
}
