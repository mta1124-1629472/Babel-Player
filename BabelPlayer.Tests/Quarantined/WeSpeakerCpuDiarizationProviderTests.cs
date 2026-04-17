using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        Assert.Contains("_patch_wespeaker_subsegment()", script, StringComparison.Ordinal);
        Assert.Contains("fbank = fbank.squeeze(0)", script, StringComparison.Ordinal);
        Assert.Contains("with redirect_stdout(captured_stdout):", script, StringComparison.Ordinal);
        Assert.Contains("print(diagnostic_output, file=sys.stderr)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WeSpeakerCpuDiarizationProvider_CheckReadiness_UsesValidatedManagedCpuRuntimeState()
    {
        var requirementsPath = Path.Combine(FindInferenceDirectory(), "cpu-requirements.txt");
        var constraintsPath = Path.Combine(FindInferenceDirectory(), "cpu-constraints.txt");
        var runtimeRoot = Path.Combine(_dir, "cpu-runtime");

        // Create platform-appropriate venv structure
        var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
        var scriptsDir = isWindows ? "Scripts" : "bin";
        var pythonExe = isWindows ? "python.exe" : "python";
        Directory.CreateDirectory(Path.Combine(runtimeRoot, ".venv", scriptsDir));

        var pythonPath = Path.Combine(runtimeRoot, ".venv", scriptsDir, pythonExe);
        File.WriteAllBytes(pythonPath, Array.Empty<byte>());

        // On Unix, set executable bit so GetPythonExecutablePath() finds it
        if (!isWindows)
        {
            try
            {
                System.IO.File.SetUnixFileMode(pythonPath,
                    System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.UserExecute |
                    System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.GroupExecute |
                    System.IO.UnixFileMode.OtherRead | System.IO.UnixFileMode.OtherExecute);
            }
            catch
            {
                // If setting permissions fails, the test may fail but that's acceptable
            }
        }

        var markerPath = Path.Combine(runtimeRoot, ".cpu-bootstrap-version");
        var markerHash = MarkerHashHelper.ComputeMarkerHash(requirementsPath, constraintsPath);
        File.WriteAllText(markerPath, markerHash);
        File.WriteAllText(
            Path.Combine(runtimeRoot, ".cpu-runtime-validation.json"),
            JsonSerializer.Serialize(new ManagedCpuRuntimeValidationRecord
            {
                MarkerHash = markerHash,
                IsValid = true,
                FailureReason = null,
            }));

        var manager = new ManagedCpuRuntimeManager(
            _log,
            cpuRuntimeRootResolver: () => runtimeRoot,
            requirementsPathResolver: () => requirementsPath,
            constraintsPathResolver: () => constraintsPath);

        var provider = new WeSpeakerCpuDiarizationProvider(_log, manager);

        var readiness = provider.CheckReadiness(new AppSettings(), null);

        Assert.True(readiness.IsReady);
        Assert.Null(readiness.BlockingReason);
    }

    [Fact]
    public void WeSpeakerCpuDiarizationProvider_CheckReadiness_SurfacesFailedValidationReason()
    {
        var requirementsPath = Path.Combine(FindInferenceDirectory(), "cpu-requirements.txt");
        var constraintsPath = Path.Combine(FindInferenceDirectory(), "cpu-constraints.txt");
        var runtimeRoot = Path.Combine(_dir, "cpu-runtime-failed");

        var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
        var scriptsDir = isWindows ? "Scripts" : "bin";
        var pythonExe = isWindows ? "python.exe" : "python";
        Directory.CreateDirectory(Path.Combine(runtimeRoot, ".venv", scriptsDir));
        File.WriteAllBytes(Path.Combine(runtimeRoot, ".venv", scriptsDir, pythonExe), Array.Empty<byte>());

        var markerHash = MarkerHashHelper.ComputeMarkerHash(requirementsPath, constraintsPath);
        File.WriteAllText(Path.Combine(runtimeRoot, ".cpu-bootstrap-version"), markerHash);
        File.WriteAllText(
            Path.Combine(runtimeRoot, ".cpu-runtime-validation.json"),
            JsonSerializer.Serialize(new ManagedCpuRuntimeValidationRecord
            {
                MarkerHash = markerHash,
                IsValid = false,
                FailureReason = "CPU runtime validation failed: ModuleNotFoundError: whisper",
                PackageVersions = new()
                {
                    ["torch"] = "2.8.0+cpu",
                    ["torchaudio"] = "2.8.0+cpu",
                    ["wespeaker"] = "0.0.0",
                },
            }));

        var manager = new ManagedCpuRuntimeManager(
            _log,
            cpuRuntimeRootResolver: () => runtimeRoot,
            requirementsPathResolver: () => requirementsPath,
            constraintsPathResolver: () => constraintsPath);

        var provider = new WeSpeakerCpuDiarizationProvider(_log, manager);
        var readiness = provider.CheckReadiness(new AppSettings(), null);

        Assert.False(readiness.IsReady);
        Assert.Contains("validation failed", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("whisper", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindInferenceDirectory()
    {
        var outputDir = Path.Combine(AppContext.BaseDirectory, "inference");
        var requirementsPath = Path.Combine(outputDir, "cpu-requirements.txt");
        if (Directory.Exists(outputDir) && File.Exists(requirementsPath))
            return outputDir;

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "inference");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "cpu-requirements.txt")))
                return candidate;

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null)
                break;
            dir = parent;
        }

        throw new InvalidOperationException($"Could not locate inference directory from {AppContext.BaseDirectory}.");
    }
}
