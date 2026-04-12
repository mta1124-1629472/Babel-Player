using Babel.Player.Services;

namespace BabelPlayer.Tests;

public class ManagedRuntimeLayoutTests
{
    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    [Fact]
    public void GetRuntimeRoot_ReturnsCorrectPath()
    {
        var expected = Path.Combine(LocalAppData, "BabelPlayer", "runtime", "managed-gpu");
        Assert.Equal(NormalizePath(expected), NormalizePath(ManagedRuntimeLayout.GetRuntimeRoot()));
    }

    [Fact]
    public void GetVenvDirectory_ReturnsCorrectPath()
    {
        var expected = Path.Combine(LocalAppData, "BabelPlayer", "runtime", "managed-gpu", ".venv");
        Assert.Equal(NormalizePath(expected), NormalizePath(ManagedRuntimeLayout.GetVenvDirectory()));
    }

    [Fact]
    public void GetManagedPythonPath_ReturnsCorrectPath()
    {
        var expected = Path.Combine(LocalAppData, "BabelPlayer", "runtime", "managed-gpu", ".venv", "Scripts", "python.exe");
        Assert.Equal(NormalizePath(expected), NormalizePath(ManagedRuntimeLayout.GetManagedPythonPath()));
    }

    [Fact]
    public void GetBootstrapMarkerPath_ReturnsCorrectPath()
    {
        var expected = Path.Combine(LocalAppData, "BabelPlayer", "runtime", "managed-gpu", ".bootstrap-version");
        Assert.Equal(NormalizePath(expected), NormalizePath(ManagedRuntimeLayout.GetBootstrapMarkerPath()));
    }

    [Fact]
    public void GetHostPidPath_ReturnsCorrectPath()
    {
        var expected = Path.Combine(LocalAppData, "BabelPlayer", "runtime", "managed-gpu", "managed-host.pid");
        Assert.Equal(NormalizePath(expected), NormalizePath(ManagedRuntimeLayout.GetHostPidPath()));
    }

    [Fact]
    public void GetModelCacheDir_ReturnsCorrectPath()
    {
        var expected = Path.Combine(LocalAppData, "BabelPlayer", "models");
        Assert.Equal(NormalizePath(expected), NormalizePath(ManagedRuntimeLayout.GetModelCacheDir()));
    }

    [Fact]
    public void GetCpuRuntimeRoot_ReturnsCorrectPath()
    {
        var expected = Path.Combine(LocalAppData, "BabelPlayer", "runtime", "managed-cpu");
        Assert.Equal(NormalizePath(expected), NormalizePath(ManagedRuntimeLayout.GetCpuRuntimeRoot()));
    }

    [Fact]
    public void GetCpuVenvDirectory_ReturnsCorrectPath()
    {
        var expected = Path.Combine(LocalAppData, "BabelPlayer", "runtime", "managed-cpu", ".venv");
        Assert.Equal(NormalizePath(expected), NormalizePath(ManagedRuntimeLayout.GetCpuVenvDirectory()));
    }

    [Fact]
    public void GetCpuPythonPath_ReturnsCorrectPath()
    {
        var expected = Path.Combine(LocalAppData, "BabelPlayer", "runtime", "managed-cpu", ".venv", "Scripts", "python.exe");
        Assert.Equal(NormalizePath(expected), NormalizePath(ManagedRuntimeLayout.GetCpuPythonPath()));
    }

    [Fact]
    public void GetCpuBootstrapMarkerPath_ReturnsCorrectPath()
    {
        var expected = Path.Combine(LocalAppData, "BabelPlayer", "runtime", "managed-cpu", ".cpu-bootstrap-version");
        Assert.Equal(NormalizePath(expected), NormalizePath(ManagedRuntimeLayout.GetCpuBootstrapMarkerPath()));
    }
}
