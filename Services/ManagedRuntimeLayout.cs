using System;
using System.IO;

namespace Babel.Player.Services;

public static class ManagedRuntimeLayout
{
    private const string ManagedGpuRuntimeFolderName = "managed-gpu";
    private const string ManagedCpuRuntimeFolderName = "managed-cpu";

    private static string RuntimeBase =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer",
            "runtime");

    // GPU runtime
    public static string GetRuntimeRoot() =>
        Path.Combine(RuntimeBase, ManagedGpuRuntimeFolderName);

    public static string GetVenvDirectory() =>
        Path.Combine(GetRuntimeRoot(), ".venv");

    public static string GetManagedPythonPath() =>
        Path.Combine(GetVenvDirectory(), "Scripts", "python.exe");

    public static string GetBootstrapMarkerPath() =>
        Path.Combine(GetRuntimeRoot(), ".bootstrap-version");

    public static string GetHostPidPath() =>
        Path.Combine(GetRuntimeRoot(), "managed-host.pid");

    // Model cache — shared across GPU and CPU runtimes, isolated from the global HF hub cache.
    // Storing models here avoids the symlink/hard-link issues that occur on Windows when
    // huggingface_hub is upgraded between releases and tries to re-verify an existing cache
    // created by an older version.
    public static string GetModelCacheDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer",
            "models");

    // CPU runtime
    public static string GetCpuRuntimeRoot() =>
        Path.Combine(RuntimeBase, ManagedCpuRuntimeFolderName);

    public static string GetCpuVenvDirectory() =>
        Path.Combine(GetCpuRuntimeRoot(), ".venv");

    public static string GetCpuPythonPath() =>
        Path.Combine(GetCpuVenvDirectory(), "Scripts", "python.exe");

    public static string GetCpuBootstrapMarkerPath() =>
        Path.Combine(GetCpuRuntimeRoot(), ".cpu-bootstrap-version");
}
