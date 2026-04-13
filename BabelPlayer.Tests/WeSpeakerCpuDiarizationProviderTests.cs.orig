using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Babel.Player.Services;
using Babel.Player.Services.Settings;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class WeSpeakerCpuDiarizationProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;

    public WeSpeakerCpuDiarizationProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-wespeaker-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "wespeaker.log"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void WeSpeakerCpuDiarizationProvider_DerivesFromPythonSubprocessServiceBase()
    {
        Assert.True(typeof(PythonSubprocessServiceBase).IsAssignableFrom(typeof(WeSpeakerCpuDiarizationProvider)));
    }

    [Fact]
    public void WeSpeakerCpuDiarizationProvider_Source_UsesManagedCpuRuntimeAndCpuOnlyWeSpeakerApi()
    {
        var script = WeSpeakerCpuDiarizationProvider.Script;

        Assert.Contains("wespeaker.load_model(\"english\")", script, StringComparison.Ordinal);
        Assert.Contains("set_device(\"cpu\")", script, StringComparison.Ordinal);
        Assert.Contains("diarize(audio_path)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WeSpeakerCpuDiarizationProvider_CheckReadiness_UsesManagedCpuRuntimeBootstrapState()
    {
        var requirementsPath = Path.Combine(FindInferenceDirectory(), "requirements.txt");
        var runtimeRoot = Path.Combine(_dir, "cpu-runtime");
        Directory.CreateDirectory(Path.Combine(runtimeRoot, ".venv", "Scripts"));

        var pythonPath = Path.Combine(runtimeRoot, ".venv", "Scripts", "python.exe");
        File.WriteAllBytes(pythonPath, Array.Empty<byte>());

        var markerPath = Path.Combine(runtimeRoot, ".cpu-bootstrap-version");
        File.WriteAllText(markerPath, ComputeMarkerHash(requirementsPath));

        var manager = new ManagedCpuRuntimeManager(
            _log,
            cpuRuntimeRootResolver: () => runtimeRoot,
            requirementsPathResolver: () => requirementsPath);

        var provider = new WeSpeakerCpuDiarizationProvider(_log, manager);

        var readiness = provider.CheckReadiness(new AppSettings(), null);

        Assert.True(readiness.IsReady);
        Assert.Null(readiness.BlockingReason);
    }

    private static string FindInferenceDirectory()
    {
        var outputDir = Path.Combine(AppContext.BaseDirectory, "inference");
        if (Directory.Exists(outputDir) && File.Exists(Path.Combine(outputDir, "requirements.txt")))
            return outputDir;

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "inference");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "requirements.txt")))
                return candidate;

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null)
                break;
            dir = parent;
        }

        throw new InvalidOperationException($"Could not locate inference directory from {AppContext.BaseDirectory}.");
    }

    private static string ComputeMarkerHash(string requirementsPath)
    {
        var content = $"python:3.11.6\n{File.ReadAllText(requirementsPath)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }
}