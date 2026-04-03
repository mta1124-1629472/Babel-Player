using System;
using System.IO;

namespace Babel.Player.Services;

public static class ManagedRuntimeLayout
{
    private const string ManagedGpuRuntimeFolderName = "managed-gpu";

    public static string GetRuntimeRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer",
            "runtime",
            ManagedGpuRuntimeFolderName);

    public static string GetVenvDirectory() => Path.Combine(GetRuntimeRoot(), ".venv");

    public static string GetManagedPythonPath() =>
        Path.Combine(GetVenvDirectory(), "Scripts", "python.exe");

    public static string GetBootstrapMarkerPath() =>
        Path.Combine(GetRuntimeRoot(), ".bootstrap-version");

    public static string GetHostPidPath() =>
        Path.Combine(GetRuntimeRoot(), "managed-host.pid");
}
