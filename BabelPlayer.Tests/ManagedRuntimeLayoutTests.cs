using System.IO;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public class ManagedRuntimeLayoutTests
{
    private static string NormalizePath(string path) => path.Replace('\\', '/');

    [Fact]
    public void GetRuntimeRoot_ReturnsCorrectPath()
    {
        var path = ManagedRuntimeLayout.GetRuntimeRoot();
        var normalized = NormalizePath(path);
        Assert.EndsWith("BabelPlayer/runtime/managed-gpu", normalized);
    }

    [Fact]
    public void GetVenvDirectory_ReturnsCorrectPath()
    {
        var path = ManagedRuntimeLayout.GetVenvDirectory();
        var normalized = NormalizePath(path);
        Assert.EndsWith("BabelPlayer/runtime/managed-gpu/.venv", normalized);
    }

    [Fact]
    public void GetManagedPythonPath_ReturnsCorrectPath()
    {
        var path = ManagedRuntimeLayout.GetManagedPythonPath();
        var normalized = NormalizePath(path);
        Assert.EndsWith("BabelPlayer/runtime/managed-gpu/.venv/Scripts/python.exe", normalized);
    }

    [Fact]
    public void GetBootstrapMarkerPath_ReturnsCorrectPath()
    {
        var path = ManagedRuntimeLayout.GetBootstrapMarkerPath();
        var normalized = NormalizePath(path);
        Assert.EndsWith("BabelPlayer/runtime/managed-gpu/.bootstrap-version", normalized);
    }

    [Fact]
    public void GetHostPidPath_ReturnsCorrectPath()
    {
        var path = ManagedRuntimeLayout.GetHostPidPath();
        var normalized = NormalizePath(path);
        Assert.EndsWith("BabelPlayer/runtime/managed-gpu/managed-host.pid", normalized);
    }

    [Fact]
    public void GetModelCacheDir_ReturnsCorrectPath()
    {
        var path = ManagedRuntimeLayout.GetModelCacheDir();
        var normalized = NormalizePath(path);
        Assert.EndsWith("BabelPlayer/models", normalized);
    }

    [Fact]
    public void GetCpuRuntimeRoot_ReturnsCorrectPath()
    {
        var path = ManagedRuntimeLayout.GetCpuRuntimeRoot();
        var normalized = NormalizePath(path);
        Assert.EndsWith("BabelPlayer/runtime/managed-cpu", normalized);
    }

    [Fact]
    public void GetCpuVenvDirectory_ReturnsCorrectPath()
    {
        var path = ManagedRuntimeLayout.GetCpuVenvDirectory();
        var normalized = NormalizePath(path);
        Assert.EndsWith("BabelPlayer/runtime/managed-cpu/.venv", normalized);
    }

    [Fact]
    public void GetCpuPythonPath_ReturnsCorrectPath()
    {
        var path = ManagedRuntimeLayout.GetCpuPythonPath();
        var normalized = NormalizePath(path);
        Assert.EndsWith("BabelPlayer/runtime/managed-cpu/.venv/Scripts/python.exe", normalized);
    }

    [Fact]
    public void GetCpuBootstrapMarkerPath_ReturnsCorrectPath()
    {
        var path = ManagedRuntimeLayout.GetCpuBootstrapMarkerPath();
        var normalized = NormalizePath(path);
        Assert.EndsWith("BabelPlayer/runtime/managed-cpu/.cpu-bootstrap-version", normalized);
    }
}
