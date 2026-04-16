using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

internal sealed record ManagedCpuRuntimeValidationRecord
{
    public required string MarkerHash { get; init; }

    public required bool IsValid { get; init; }

    public string? FailureReason { get; init; }

    public Dictionary<string, string?> PackageVersions { get; init; } = [];
}

internal sealed record ManagedCpuRuntimeInspection(
    ManagedCpuState State,
    bool NeedsBootstrap,
    string? Detail,
    ManagedCpuRuntimeValidationRecord? ValidationRecord = null);

public sealed class ManagedCpuRuntimeManager
{
    private const string PythonVersion = "3.11.6";
    private static readonly string DebugLogPath = ResolveDebugLogPath();
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly AppLog _log;
    private readonly Func<string?> _uvResolver;
    private readonly string _cpuRuntimeRoot;
    private readonly Func<string> _requirementsPathResolver;
    private readonly Func<string> _constraintsPathResolver;

    public ManagedCpuRuntimeManager(
        AppLog log,
        Func<string?>? uvResolver = null,
        Func<string>? cpuRuntimeRootResolver = null,
        Func<string>? requirementsPathResolver = null,
        Func<string>? constraintsPathResolver = null)
    {
        _log = log;
        _uvResolver = uvResolver ?? DependencyLocator.FindUv;
        _cpuRuntimeRoot = (cpuRuntimeRootResolver ?? ManagedRuntimeLayout.GetCpuRuntimeRoot)();
        _requirementsPathResolver = requirementsPathResolver ?? ResolveCpuRequirementsPath;
        _constraintsPathResolver = constraintsPathResolver ?? ResolveCpuConstraintsPath;
    }

    public ManagedCpuState State { get; private set; } = ManagedCpuState.NotInstalled;

    public string? FailureReason { get; private set; }

    /// <summary>
    /// The most recent status line from the bootstrap process (e.g., "Downloading torch (2.4 GB)").
    /// Updated live during installation.
    /// </summary>
    public string BootstrapStatusLine { get; private set; } = string.Empty;

    /// <summary>
    /// True when the CPU venv needs to be (re)installed — either missing, stale, or never validated.
    /// </summary>
    private bool? _cachedNeedsBootstrap;
    private readonly object _bootstrapCacheLock = new();

    public Task<bool> CheckNeedsBootstrapAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_bootstrapCacheLock)
        {
            if (_cachedNeedsBootstrap.HasValue)
                return Task.FromResult(_cachedNeedsBootstrap.Value);
        }

        var inspection = InspectRuntimeState();
        CacheNeedsBootstrap(inspection.NeedsBootstrap);
        return Task.FromResult(inspection.NeedsBootstrap);
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
        var ensureInstalledStopwatch = Stopwatch.StartNew();
        var inspection = InspectRuntimeState();
        CacheNeedsBootstrap(inspection.NeedsBootstrap);
        if (!inspection.NeedsBootstrap)
        {
            ApplyInspection(inspection, logReadyState: true);
            ensureInstalledStopwatch.Stop();
            return;
        }

        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            // Re-check under lock in case a concurrent call already bootstrapped.
            inspection = InspectRuntimeState();
            CacheNeedsBootstrap(inspection.NeedsBootstrap);
            if (!inspection.NeedsBootstrap)
            {
                ApplyInspection(inspection, logReadyState: true);
                ensureInstalledStopwatch.Stop();
                return;
            }

            await RunBootstrapAsync(onStatusLine, cancellationToken);
        }
        finally
        {
            InstallGate.Release();
        }

        ensureInstalledStopwatch.Stop();
        var finalInspection = InspectRuntimeState();
    }

    /// <summary>
    /// Returns the captured managed CPU runtime root for this manager instance.
    /// The root is resolved once in the constructor so Python and marker paths stay consistent.
    /// </summary>
    public string RuntimeRoot => _cpuRuntimeRoot;

    public string GetPythonExecutablePath() =>
        Path.Combine(
            RuntimeRoot,
            ".venv",
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "Scripts" : "bin",
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "python.exe" : "python");

    public string GetBootstrapMarkerPath() =>
        Path.Combine(RuntimeRoot, ".cpu-bootstrap-version");

    public string GetValidationRecordPath() =>
        Path.Combine(RuntimeRoot, ".cpu-runtime-validation.json");

    internal ManagedCpuRuntimeInspection InspectRuntimeState()
    {
        var requirementsPath = _requirementsPathResolver();
        if (!File.Exists(requirementsPath))
        {
            return new ManagedCpuRuntimeInspection(
                ManagedCpuState.Failed,
                NeedsBootstrap: false,
                $"CPU requirements file not found: {requirementsPath}");
        }

        var constraintsPath = _constraintsPathResolver();
        if (!File.Exists(constraintsPath))
        {
            return new ManagedCpuRuntimeInspection(
                ManagedCpuState.Failed,
                NeedsBootstrap: false,
                $"CPU constraints file not found: {constraintsPath}");
        }

        var pythonPath = GetPythonExecutablePath();
        if (!File.Exists(pythonPath))
        {
            return new ManagedCpuRuntimeInspection(
                ManagedCpuState.NotInstalled,
                NeedsBootstrap: true,
                "Managed CPU runtime is not installed.");
        }

        var markerPath = GetBootstrapMarkerPath();
        if (!File.Exists(markerPath))
        {
            return new ManagedCpuRuntimeInspection(
                ManagedCpuState.NotInstalled,
                NeedsBootstrap: true,
                "Managed CPU runtime bootstrap marker is missing.");
        }

        string expectedHash;
        try
        {
            expectedHash = ComputeMarkerHash(requirementsPath, constraintsPath);
        }
        catch (Exception ex)
        {
            return new ManagedCpuRuntimeInspection(
                ManagedCpuState.Failed,
                NeedsBootstrap: false,
                $"Failed to compute managed CPU runtime marker hash: {ex.Message}");
        }

        string storedHash;
        try
        {
            storedHash = File.ReadAllText(markerPath).Trim();
        }
        catch (Exception)
        {
            return new ManagedCpuRuntimeInspection(
                ManagedCpuState.NotInstalled,
                NeedsBootstrap: true,
                "Managed CPU runtime bootstrap marker could not be read.");
        }

        if (!string.Equals(storedHash, expectedHash, StringComparison.Ordinal))
        {
            return new ManagedCpuRuntimeInspection(
                ManagedCpuState.NotInstalled,
                NeedsBootstrap: true,
                "Managed CPU runtime dependencies changed and require reinstall.");
        }

        var validationPath = GetValidationRecordPath();
        if (!File.Exists(validationPath))
        {
            return new ManagedCpuRuntimeInspection(
                ManagedCpuState.NotInstalled,
                NeedsBootstrap: true,
                "Managed CPU runtime has not been validated yet.");
        }

        ManagedCpuRuntimeValidationRecord? validationRecord;
        try
        {
            validationRecord = JsonSerializer.Deserialize<ManagedCpuRuntimeValidationRecord>(
                File.ReadAllText(validationPath),
                JsonOptions);
        }
        catch (Exception)
        {
            return new ManagedCpuRuntimeInspection(
                ManagedCpuState.NotInstalled,
                NeedsBootstrap: true,
                "Managed CPU runtime validation record is unreadable.");
        }

        if (validationRecord is null || !string.Equals(validationRecord.MarkerHash, expectedHash, StringComparison.Ordinal))
        {
            return new ManagedCpuRuntimeInspection(
                ManagedCpuState.NotInstalled,
                NeedsBootstrap: true,
                "Managed CPU runtime validation record is stale.");
        }

        if (!validationRecord.IsValid)
        {
            return new ManagedCpuRuntimeInspection(
                ManagedCpuState.Failed,
                NeedsBootstrap: false,
                validationRecord.FailureReason ?? "Managed CPU runtime validation failed.",
                validationRecord);
        }

        return new ManagedCpuRuntimeInspection(
            ManagedCpuState.Ready,
            NeedsBootstrap: false,
            "Managed CPU runtime validated and ready.",
            validationRecord);
    }

    private async Task RunBootstrapAsync(
        Action<string>? onStatusLine,
        CancellationToken cancellationToken)
    {
        var uvPath = _uvResolver();
        if (string.IsNullOrWhiteSpace(uvPath))
        {
            SetFailedState(
                $"uv.exe was not found. Bundle tools\\{WindowsPackagingPaths.NativeRidFolder}\\uv.exe or install uv on PATH.",
                needsBootstrap: true);
            return;
        }

        var requirementsPath = _requirementsPathResolver();
        if (!File.Exists(requirementsPath))
        {
            SetFailedState(
                $"CPU requirements file not found: {requirementsPath}",
                needsBootstrap: false);
            return;
        }

        var constraintsPath = _constraintsPathResolver();
        if (!File.Exists(constraintsPath))
        {
            SetFailedState(
                $"CPU constraints file not found: {constraintsPath}",
                needsBootstrap: false);
            return;
        }

        var runtimeRoot = RuntimeRoot;
        var venvDir = Path.Combine(runtimeRoot, ".venv");
        var pythonPath = GetPythonExecutablePath();
        var markerPath = GetBootstrapMarkerPath();
        Directory.CreateDirectory(runtimeRoot);

        State = ManagedCpuState.Installing;
        FailureReason = null;
        BootstrapStatusLine = "Installing managed CPU runtime...";
        onStatusLine?.Invoke(BootstrapStatusLine);

        _log.Info(
            $"CPU runtime bootstrap starting: venv={venvDir}, requirements={requirementsPath}, constraints={constraintsPath}, uv={uvPath}");

        try
        {
            await RunProcessAsync(
                uvPath,
                Path.GetDirectoryName(venvDir) ?? AppContext.BaseDirectory,
                cancellationToken,
                null,
                "venv",
                "--clear",
                "--python",
                PythonVersion,
                venvDir);

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
                "setuptools==80.9.0",
                "wheel");

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
                "--no-build-isolation",
                "--python",
                pythonPath,
                "-r",
                requirementsPath,
                "-c",
                constraintsPath);
        }
        catch (InvalidOperationException ex)
        {
            SetFailedState(
                $"CPU runtime bootstrap failed: {ex.Message}",
                needsBootstrap: true,
                bootstrapStatusLine: "Managed CPU runtime bootstrap failed.");
            return;
        }

        var markerHash = ComputeMarkerHash(requirementsPath, constraintsPath);
        await File.WriteAllTextAsync(markerPath, markerHash, cancellationToken).ConfigureAwait(false);

        BootstrapStatusLine = "Validating managed CPU runtime imports...";
        onStatusLine?.Invoke(BootstrapStatusLine);

        ManagedCpuRuntimeValidationRecord validationRecord;
        try
        {
            validationRecord = await ValidateInstalledRuntimeAsync(markerHash, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            validationRecord = new ManagedCpuRuntimeValidationRecord
            {
                MarkerHash = markerHash,
                IsValid = false,
                FailureReason = $"CPU runtime validation failed unexpectedly: {ex.Message}",
            };
        }

        await PersistValidationRecordAsync(validationRecord, cancellationToken).ConfigureAwait(false);

        if (!validationRecord.IsValid)
        {
            var formattedVersions = FormatPackageVersions(validationRecord.PackageVersions);
            if (!string.IsNullOrWhiteSpace(formattedVersions))
                _log.Warning($"Managed CPU runtime package versions at validation failure: {formattedVersions}");

            SetFailedState(
                validationRecord.FailureReason ?? "Managed CPU runtime validation failed.",
                needsBootstrap: false,
                bootstrapStatusLine: "Managed CPU runtime validation failed.");
            return;
        }

        CacheNeedsBootstrap(false);
        State = ManagedCpuState.Ready;
        FailureReason = null;
        BootstrapStatusLine = "Managed CPU runtime validated and ready.";
        _log.Info($"CPU runtime bootstrap completed and validated at {venvDir}.");
    }

    private void ApplyInspection(ManagedCpuRuntimeInspection inspection, bool logReadyState)
    {
        State = inspection.State;
        FailureReason = inspection.State == ManagedCpuState.Failed ? inspection.Detail : null;
        BootstrapStatusLine = inspection.State switch
        {
            ManagedCpuState.Ready => "Managed CPU runtime validated and ready.",
            ManagedCpuState.Failed => "Managed CPU runtime validation failed.",
            ManagedCpuState.Installing => "Installing managed CPU runtime...",
            _ => "Managed CPU runtime bootstrap required.",
        };

        if (inspection.State == ManagedCpuState.Ready)
        {
            if (logReadyState)
                _log.Info("CPU runtime: already installed and validated.");
            return;
        }

        if (inspection.State == ManagedCpuState.Failed && !string.IsNullOrWhiteSpace(inspection.Detail))
        {
            _log.Warning($"Managed CPU runtime remains unavailable: {inspection.Detail}");
        }
    }

    private void SetFailedState(
        string message,
        bool needsBootstrap,
        string? bootstrapStatusLine = null)
    {
        State = ManagedCpuState.Failed;
        FailureReason = message;
        BootstrapStatusLine = bootstrapStatusLine ?? "Managed CPU runtime validation failed.";
        CacheNeedsBootstrap(needsBootstrap);
        _log.Warning(message);
    }

    private void CacheNeedsBootstrap(bool needsBootstrap)
    {
        lock (_bootstrapCacheLock)
        {
            _cachedNeedsBootstrap = needsBootstrap;
        }
    }

    private async Task PersistValidationRecordAsync(
        ManagedCpuRuntimeValidationRecord validationRecord,
        CancellationToken cancellationToken)
    {
        var validationPath = GetValidationRecordPath();
        var payload = JsonSerializer.Serialize(validationRecord, JsonOptions);
        await File.WriteAllTextAsync(validationPath, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ManagedCpuRuntimeValidationRecord> ValidateInstalledRuntimeAsync(
        string markerHash,
        CancellationToken cancellationToken)
    {
        const string validationScript = """
            import json
            import traceback
            from importlib import metadata

            payload = {
                "success": False,
                "message": None,
                "error": None,
                "traceback": None,
                "packages": {},
            }

            for package_name in [
                "torch",
                "torchaudio",
                "wespeaker",
                "onnxruntime",
                "openai-whisper",
                "peft",
                "scikit-learn",
            ]:
                try:
                    payload["packages"][package_name] = metadata.version(package_name)
                except Exception:
                    payload["packages"][package_name] = None

            try:
                import torch
                import torchaudio
                import wespeaker

                payload["packages"]["torch"] = getattr(torch, "__version__", payload["packages"]["torch"])
                payload["packages"]["torchaudio"] = getattr(torchaudio, "__version__", payload["packages"]["torchaudio"])
                payload["packages"]["wespeaker"] = getattr(wespeaker, "__version__", payload["packages"]["wespeaker"])
                payload["success"] = True
                payload["message"] = "Managed CPU runtime imports validated."
            except Exception as exc:
                payload["error"] = f"{type(exc).__name__}: {exc}"
                payload["traceback"] = traceback.format_exc()

            print(json.dumps(payload))
            """;

        var pythonPath = GetPythonExecutablePath();

        // Write to a temp file rather than passing as -c to avoid Windows command-line
        // quoting issues with multi-line scripts containing double-quoted string literals.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"babel-cpu-validate_{Guid.NewGuid():N}.py");
        ManagedCpuProcessCapture result;
        try
        {
            await File.WriteAllTextAsync(scriptPath, validationScript, cancellationToken)
                .ConfigureAwait(false);

            result = await RunProcessCaptureAsync(
                    pythonPath,
                    AppContext.BaseDirectory,
                    cancellationToken,
                    scriptPath)
                .ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort cleanup */ }
        }

        if (result.ExitCode != 0)
        {
            return new ManagedCpuRuntimeValidationRecord
            {
                MarkerHash = markerHash,
                IsValid = false,
                FailureReason = $"CPU runtime validation failed: {result.Stderr.Trim()}",
            };
        }

        ManagedCpuValidationProbePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ManagedCpuValidationProbePayload>(result.Stdout, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new ManagedCpuRuntimeValidationRecord
            {
                MarkerHash = markerHash,
                IsValid = false,
                FailureReason = $"CPU runtime validation produced unreadable output: {ex.Message}",
            };
        }

        if (payload is null)
        {
            return new ManagedCpuRuntimeValidationRecord
            {
                MarkerHash = markerHash,
                IsValid = false,
                FailureReason = "CPU runtime validation returned no payload.",
            };
        }

        if (!payload.Success)
        {
            var failureReason = payload.Error ?? payload.Message ?? "Managed CPU runtime validation failed.";
            if (!string.IsNullOrWhiteSpace(payload.Traceback))
                _log.Warning(payload.Traceback.Trim());

            return new ManagedCpuRuntimeValidationRecord
            {
                MarkerHash = markerHash,
                IsValid = false,
                FailureReason = $"CPU runtime validation failed: {failureReason}",
                PackageVersions = payload.Packages,
            };
        }

        _log.Info($"Managed CPU runtime validated with packages: {FormatPackageVersions(payload.Packages)}");
        return new ManagedCpuRuntimeValidationRecord
        {
            MarkerHash = markerHash,
            IsValid = true,
            FailureReason = null,
            PackageVersions = payload.Packages,
        };
    }

    /// <summary>
    /// Starts an external process, streams its standard output to the application log and to an optional status-line callback, waits for the process to exit, and fails the operation if the process cannot be started or exits with a non-zero code.
    /// </summary>
    /// <param name="fileName">Executable to run.</param>
    /// <param name="workingDirectory">Working directory for the process.</param>
    /// <param name="cancellationToken">Token used to cancel reading output and waiting for process exit; cancellation will abort the operation.</param>
    /// <param name="onStatusLine">Optional callback invoked for each non-empty stdout line produced by the process.</param>
    /// <param name="arguments">Arguments passed to the process.</param>
    /// <remarks>
    /// This method logs each stdout line at info level and invokes <paramref name="onStatusLine"/> for non-empty lines. If the process cannot be started or exits with a non-zero exit code, an <see cref="InvalidOperationException"/> is thrown. Operation cancellation will propagate via the provided <paramref name="cancellationToken"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the process fails to start or when it exits with a non-zero exit code.</exception>
    private async Task RunProcessAsync(
        string fileName,
        string workingDirectory,
        CancellationToken cancellationToken,
        Action<string>? onStatusLine,
        params string[] arguments)
    {
        // Use ArgumentList so the runtime handles all quoting; paths with spaces are safe.
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        _log.Info($"Running CPU runtime process: {fileName}");
        foreach (var arg in arguments)
        {
            _log.Info($"  {arg}");
        }

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
        {
            throw new InvalidOperationException(
                $"Process '{fileName} {ProcessArgFormatter.FormatArgs(arguments)}' failed with exit code {process.ExitCode}: {stderr}");
        }

        if (!string.IsNullOrWhiteSpace(stderr))
            _log.Info(stderr.Trim());
    }

    /// <summary>
    /// Starts a process with the specified executable and arguments, waits for it to exit, and captures its exit code, standard output, and standard error.
    /// </summary>
    /// <param name="fileName">Path to the executable to run.</param>
    /// <param name="workingDirectory">Working directory for the process.</param>
    /// <param name="cancellationToken">Token to observe for cancellation while waiting for the process and reading its output.</param>
    /// <param name="arguments">Arguments passed to the process; each element is added to the process argument list so quoting/escaping is handled by the runtime.</param>
    /// <returns>
    /// A <see cref="ManagedCpuProcessCapture"/> containing the process exit code, the full standard output, and the full standard error.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown if the process could not be started.</exception>
    private async Task<ManagedCpuProcessCapture> RunProcessCaptureAsync(
        string fileName,
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        // Use ArgumentList so the runtime handles all quoting; paths with spaces are safe.
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        _log.Info($"Running CPU runtime capture process: {fileName} {ProcessArgFormatter.FormatArgs(arguments)}");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new ManagedCpuProcessCapture(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }



    // Marker format includes PythonVersion plus labeled requirements/constraints bodies so
    // upgrades and manifest edits invalidate the venv consistently with tests.
    private string ComputeMarkerHash(string requirementsPath, string constraintsPath)
    {
        var requirementsContent = File.ReadAllText(requirementsPath);
        var constraintsContent = File.ReadAllText(constraintsPath);
        var content = $"python:{PythonVersion}\n[requirements]\n{requirementsContent}\n[constraints]\n{constraintsContent}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private static string FormatPackageVersions(IReadOnlyDictionary<string, string?> packageVersions)
    {
        var entries = new List<string>();
        foreach (var (packageName, version) in packageVersions)
        {
            entries.Add(string.IsNullOrWhiteSpace(version)
                ? $"{packageName}=missing"
                : $"{packageName}={version}");
        }

        return string.Join(", ", entries);
    }

    private sealed record ManagedCpuProcessCapture(int ExitCode, string Stdout, string Stderr);

    private sealed record ManagedCpuValidationProbePayload
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("traceback")]
        public string? Traceback { get; init; }

        [JsonPropertyName("packages")]
        public Dictionary<string, string?> Packages { get; init; } = [];
    }

    private static string ResolveCpuRequirementsPath() =>
        Path.Combine(AppContext.BaseDirectory, "inference", "cpu-requirements.txt");

    private static string ResolveCpuConstraintsPath() =>
        Path.Combine(AppContext.BaseDirectory, "inference", "cpu-constraints.txt");

    private static string ResolveDebugLogPath()
    {
        var envPath = Environment.GetEnvironmentVariable("BABEL_DEBUG_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return envPath;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Babel-Player.sln")))
                return Path.Combine(dir.FullName, "debug-f76224.log");
            dir = dir.Parent;
        }

        return Path.Combine(Environment.CurrentDirectory, "debug-f76224.log");
    }

    private static void WriteDebugLog(string runId, string hypothesisId, string location, string message, object data)
    {
        var payload = new
        {
            sessionId = "f76224",
            runId,
            hypothesisId,
            location,
            message,
            data,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        try
        {
            var line = JsonSerializer.Serialize(payload);
            File.AppendAllText(DebugLogPath, line + Environment.NewLine);
        }
        catch
        {
            // Swallow debug log failures.
        }
    }
}
