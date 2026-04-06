using System;
using System.IO;

namespace Babel.Player.Services;

public static class ManagedRuntimeLayout
{
    private static string RuntimeBase =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer",
            "runtime");

    // ── GPU (managed venv + host process) ─────────────────────────────────

    public static string GetRuntimeRoot() => Path.Combine(RuntimeBase, "managed-gpu");

    public static string GetVenvDirectory() => Path.Combine(GetRuntimeRoot(), ".venv");

    public static string GetManagedPythonPath() =>
        Path.Combine(GetVenvDirectory(), "Scripts", "python.exe");

    public static string GetBootstrapMarkerPath() =>
        Path.Combine(GetRuntimeRoot(), ".bootstrap-version");

    public static string GetHostPidPath() =>
        Path.Combine(GetRuntimeRoot(), "managed-host.pid");

    // ── CPU (managed venv, no host process) ───────────────────────────────

    public static string GetCpuRuntimeRoot() => Path.Combine(RuntimeBase, "managed-cpu");

    public static string GetCpuVenvDirectory() => Path.Combine(GetCpuRuntimeRoot(), ".venv");

    public static string GetCpuPythonPath() =>
        Path.Combine(GetCpuVenvDirectory(), "Scripts", "python.exe");

    public static string GetCpuBootstrapMarkerPath() =>
        Path.Combine(GetCpuRuntimeRoot(), ".cpu-bootstrap-version");
}
