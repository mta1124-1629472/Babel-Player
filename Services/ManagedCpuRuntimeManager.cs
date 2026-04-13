using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public enum ManagedCpuState
{
    NotInstalled,
    Installing,
    Ready,
    Failed,
}

public sealed class ManagedCpuRuntimeManager
{
    internal const string PythonVersion = "3.11.6";
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    private readonly AppLog _log;
    private readonly Func<string?> _uvResolver;
    private readonly string _cpuRuntimeRoot;
    private readonly Func<string> _requirementsPathResolver;

    public ManagedCpuRuntimeManager(
        AppLog log,
        Func<string?>? uvResolver = null,
        Func<string>? cpuRuntimeRootResolver = null,
        Func<string>? requirementsPathResolver = null)
    {
        _log = log;
        _uvResolver = uvResolver ?? DependencyLocator.FindUv;
        _cpuRuntimeRoot = (cpuRuntimeRootResolver ?? ManagedRuntimeLayout.GetCpuRuntimeRoot)();
        _requirementsPathResolver = requirementsPathResolver ?? ResolveCpuRequirementsPath;
    }

    public ManagedCpuState State { get; private set; } = ManagedCpuState.NotInstalled;

    public string? FailureReason { get; private set; }

    /// <summary>
    /// The most recent status line from the bootstrap process (e.g., "Downloading torch (2.4 GB)").
    /// Updated live during installation.
    /// </summary>
    public string BootstrapStatusLine { get; private set; } = string.Empty;

    /// <summary>
    /// True when the CPU venv needs to be (re)installed — either missing or requirements changed.
    /// </summary>
    public bool NeedsBootstrap
    {
        get
        {
            var pythonPath = GetPythonExecutablePath();
            if (!File.Exists(pythonPath))
                return true;
            var markerPath = GetBootstrapMarkerPath();
            if (!File.Exists(markerPath))
                return true;
            try
            {
                var stored = File.ReadAllText(markerPath).Trim();
                var requirementsPath = _requirementsPathResolver();
                return !File.Exists(requirementsPath)
                    || !string.Equals(stored, ComputeMarkerHash(requirementsPath), StringComparison.Ordinal);
            }
            catch
            {
                return true;
            }
        }
    }

    public void RequestEnsureInstalled(Action<string>? onStatusLine = null)
    {
        BackgroundTaskObserver.Observe(
            EnsureInstalledAsync(onStatusLine),
            _log,
            "CPU runtime bootstrap");
    }

    public async Task EnsureInstalledAsync(
        Action<string>? onStatusLine = null,
        CancellationToken cancellationToken = default)
    {
        if (!NeedsBootstrap)
        {
            State = ManagedCpuState.Ready;
            _log.Info("CPU runtime: already installed and up to date.");
            return;
        }

        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            // Re-check under lock in case a concurrent call already bootstrapped.
            if (!NeedsBootstrap)
            {
                State = ManagedCpuState.Ready;
                return;
            }

            await RunBootstrapAsync(onStatusLine, cancellationToken);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    /// <summary>
    /// Returns the captured managed CPU runtime root for this manager instance.
    /// The root is resolved once in the constructor so Python and marker paths stay consistent.
    /// </summary>
    public string RuntimeRoot => _cpuRuntimeRoot;

    public string GetPythonExecutablePath() =>
        Path.Combine(RuntimeRoot, ".venv", "Scripts", "python.exe");

    public string GetBootstrapMarkerPath() =>
        Path.Combine(RuntimeRoot, ".cpu-bootstrap-version");

    private async Task RunBootstrapAsync(
        Action<string>? onStatusLine,
        CancellationToken cancellationToken)
    {
        var uvPath = _uvResolver();
        if (string.IsNullOrWhiteSpace(uvPath))
        {
            Fail("uv.exe was not found. Bundle tools\\win-x64\\uv.exe or install uv on PATH.");
            return;
        }

        var requirementsPath = _requirementsPathResolver();
        if (!File.Exists(requirementsPath))
        {
            Fail($"CPU requirements file not found: {requirementsPath}");
            return;
        }

        var runtimeRoot = RuntimeRoot;
        var venvDir = Path.Combine(runtimeRoot, ".venv");
        var pythonPath = GetPythonExecutablePath();
        var markerPath = GetBootstrapMarkerPath();
        Directory.CreateDirectory(runtimeRoot);

        State = ManagedCpuState.Installing;
        _log.Info(
            $"CPU runtime bootstrap starting: venv={venvDir}, requirements={requirementsPath}, uv={uvPath}");

        try
        {
            // Step 1: create venv with pinned Python version.
            await RunProcessAsync(
                uvPath,
                Path.GetDirectoryName(venvDir) ?? AppContext.BaseDirectory,
                cancellationToken,
                null,   // venv creation is fast — no need to surface each line
                "venv",
                "--clear",
                "--python",
                PythonVersion,
                venvDir);

            // Step 2: install CPU packages — stream output for live progress.
            await RunProcessAsync(
                uvPath,
                AppContext.BaseDirectory,
                cancellationToken,
                line =>
                {
                    BootstrapStatusLine = line;
                    onStatusLine?.Invoke(line);
                },
                "pip",
                "install",
                "--python",
                pythonPath,
                "-r",
                requirementsPath);
        }
        catch (InvalidOperationException ex)
        {
            Fail($"CPU runtime bootstrap failed: {ex.Message}");
            return;
        }

        await File.WriteAllTextAsync(
            markerPath,
            ComputeMarkerHash(requirementsPath),
            cancellationToken);

        State = ManagedCpuState.Ready;
        FailureReason = null;
        _log.Info($"CPU runtime bootstrap completed at {venvDir}.");
    }

    private void Fail(string message)
    {
        State = ManagedCpuState.Failed;
        FailureReason = message;
        _log.Warning(message);
    }

    private async Task RunProcessAsync(
        string fileName,
        string workingDirectory,
        CancellationToken cancellationToken,
        Action<string>? onStatusLine,
        params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        _log.Info(
            $"Running CPU runtime process: file={fileName}, args={string.Join(' ', arguments)}");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            _log.Info(line);
            if (!string.IsNullOrWhiteSpace(line))
                onStatusLine?.Invoke(line);
        }

        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;

        _log.Info($"CPU runtime process exited: file={fileName}, exit_code={process.ExitCode}");

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Process '{fileName} {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}: {stderr}");

        if (!string.IsNullOrWhiteSpace(stderr))
            _log.Info(stderr.Trim());
    }

    // Marker format: "python:{version}\n{requirements_content}"
    // Including PythonVersion ensures a version upgrade invalidates the existing venv.
    public string ComputeMarkerHash(string requirementsPath)
    {
        var content = $"python:{PythonVersion}\n{File.ReadAllText(requirementsPath)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private static string ResolveCpuRequirementsPath() =>
        Path.Combine(AppContext.BaseDirectory, "inference", "requirements.txt");
}