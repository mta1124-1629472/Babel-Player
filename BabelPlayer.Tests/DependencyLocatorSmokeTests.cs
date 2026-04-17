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

    [Fact]
    [Trait("Category", "Smoke")]
    public void FindFfmpeg_ResolvesBundledToolsRidPath()
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
    public void FindFfprobe_ResolvesBundledToolsRidPath()
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

        if (File.Exists(commandPath))
            return;

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

        _createdPaths.Add(commandPath);
    }
}
