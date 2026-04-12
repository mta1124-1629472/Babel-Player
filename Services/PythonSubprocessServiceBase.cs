using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Base class for services that execute inference work by spawning Python subprocesses.
///
/// Eliminates boilerplate shared by all five AI pipeline services:
///   - Managed CPU runtime bootstrap and Python executable resolution
///   - Temp-script write → process spawn → stdout/stderr capture → cleanup
///   - Async wait with <see cref="CancellationToken"/> support
///   - Uniform failure logging and exception throwing
///
/// Derived classes provide the script content and call-specific argument values;
/// this class handles the execution lifecycle.
/// </summary>
public abstract class PythonSubprocessServiceBase
{
    private const int ProcessTerminationRetryAttempts = 3;
    private static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromSeconds(2);

    protected readonly AppLog Log;
    protected readonly string PythonPath;
    private readonly ManagedCpuRuntimeManager? _cpuRuntimeManager;

    protected PythonSubprocessServiceBase(AppLog log)
        : this(log, new ManagedCpuRuntimeManager(log))
    {
    }

    protected PythonSubprocessServiceBase(
        AppLog log,
        ManagedCpuRuntimeManager cpuRuntimeManager)
        : this(log, cpuRuntimeManager.GetPythonExecutablePath(), cpuRuntimeManager)
    {
        // ManagedCpuRuntimeManager pins its runtime root at construction so the subprocess
        // Python path and bootstrap marker checks resolve against the same managed CPU venv.
    }

    protected PythonSubprocessServiceBase(
        AppLog log,
        string pythonPath,
        ManagedCpuRuntimeManager? cpuRuntimeManager = null)
    {
        if (cpuRuntimeManager is not null)
        {
            var managedPythonPath = cpuRuntimeManager.GetPythonExecutablePath();
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!string.Equals(
                    Path.GetFullPath(pythonPath),
                    Path.GetFullPath(managedPythonPath),
                    comparison))
            {
                throw new ArgumentException(
                    $"The supplied pythonPath ('{pythonPath}') must match the managed runtime Python executable path " +
                    $"('{managedPythonPath}') when a {nameof(ManagedCpuRuntimeManager)} is provided.",
                    nameof(pythonPath));
            }
        }

        Log = log;
        PythonPath = pythonPath;
        _cpuRuntimeManager = cpuRuntimeManager;
    }

    /// <summary>Captures the output of a single Python subprocess invocation.</summary>
    /// <param name="ExitCode">Process exit code (0 = success).</param>
    /// <param name="Stdout">Captured standard output text.</param>
    /// <param name="Stderr">Captured standard error text.</param>
    /// <param name="ElapsedMs">
    /// Wall-clock milliseconds measured over the awaited execution phase after process
    /// startup, including stdin completion and stdout/stderr drain tasks, through process
    /// exit. Excludes script write / process spawn overhead, so it closely approximates
    /// the duration observed by the caller after launch.
    /// </param>
    public sealed record ScriptResult(int ExitCode, string Stdout, string Stderr, long ElapsedMs = 0);

    /// <summary>
    /// Writes <paramref name="scriptContent"/> to a uniquely-named temp file, runs it
    /// under <see cref="PythonPath"/> with <paramref name="arguments"/> appended after
    /// the script path, captures stdout/stderr, then deletes the temp file.
    ///
    /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/>
    /// is signalled during the wait.
    /// </summary>
    /// <param name="scriptContent">Python source to execute.</param>
    /// <param name="arguments">Structured argument values that follow the script path.</param>
    /// <param name="scriptPrefix">
    /// Short label used in the temp filename (e.g. "transcribe", "translate_seg").
    /// Defaults to "script".
    /// </param>
    /// <param name="standardInput">
    /// Optional UTF-8 text piped to the script via stdin. Use for large or user-provided
    /// text instead of placing it on the command line.
    /// </param>
    /// <param name="environmentVariables">
    /// Optional extra environment variables injected into the spawned process. These are
    /// merged into the inherited environment — prefer this over command-line arguments for
    /// secrets (env vars are not visible in process listings).
    /// </param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    protected virtual async Task<ScriptResult> RunPythonScriptAsync(
        string scriptContent,
        IReadOnlyList<string>? arguments = null,
        string scriptPrefix = "script",
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        // Guard against path traversal via scriptPrefix — fast arg-check before any I/O.
        if (!IsValidScriptPrefix(scriptPrefix))
            throw new ArgumentException(
                $"scriptPrefix must contain only alphanumeric characters, hyphens, and underscores. Got: {scriptPrefix}",
                nameof(scriptPrefix));

        // Honour cancellation before triggering the (potentially expensive) bootstrap.
        cancellationToken.ThrowIfCancellationRequested();

        await EnsurePythonRuntimeReadyAsync(cancellationToken).ConfigureAwait(false);

        var scriptPath = Path.Combine(Path.GetTempPath(), $"{scriptPrefix}_{Guid.NewGuid():N}.py");

        try
        {
            await File.WriteAllTextAsync(scriptPath, scriptContent, cancellationToken);

            var psi = new ProcessStartInfo
            {
                FileName               = PythonPath,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = standardInput is not null,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add(scriptPath);
            foreach (var argument in arguments ?? Array.Empty<string>())
                psi.ArgumentList.Add(argument);
            if (environmentVariables is not null)
                foreach (var (key, value) in environmentVariables)
                    psi.Environment[key] = value;

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start Python process ({scriptPrefix}).");

            var stdinTask  = WriteStandardInputAsync(proc, standardInput, cancellationToken);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

            // ── Timing: measure the wall-clock duration of the inference subprocess ──
            var sw = Stopwatch.StartNew();
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask, stdinTask);
                await proc.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Cancellation requested - kill the process to prevent zombie
                try
                {
                    proc.Kill(entireProcessTree: true);

                    // Give the process multiple opportunities to terminate
                    var terminated = false;
                    for (int attempt = 0; attempt < ProcessTerminationRetryAttempts && !terminated; attempt++)
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(ProcessTerminationTimeout);
                            await proc.WaitForExitAsync(cts.Token);
                            terminated = true;
                        }
                        catch (OperationCanceledException)
                        {
                            // Process didn't terminate within timeout, try again
                            if (attempt < ProcessTerminationRetryAttempts - 1)
                                proc.Kill(entireProcessTree: true);
                        }
                    }

                    if (!terminated)
                    {
                        try { proc.Kill(entireProcessTree: true); }
                        catch { /* best effort */ }
                    }
                }
                catch
                {
                    // Best effort — process might already be dead or inaccessible
                }
                throw;
            }
            finally
            {
                sw.Stop();
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return new ScriptResult(proc.ExitCode, stdout, stderr, sw.ElapsedMilliseconds);
        }
        finally
        {
            if (File.Exists(scriptPath))
                File.Delete(scriptPath);
        }
    }

    private async Task EnsurePythonRuntimeReadyAsync(CancellationToken cancellationToken)
    {
        if (_cpuRuntimeManager is null)
        {
            if (string.IsNullOrWhiteSpace(PythonPath) || !File.Exists(PythonPath))
            {
                throw new InvalidOperationException(
                    "Python subprocess runtime is not ready. A valid Python executable path must be provided.");
            }

            return;
        }

        await _cpuRuntimeManager.EnsureInstalledAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        if (_cpuRuntimeManager.State != ManagedCpuState.Ready || !File.Exists(PythonPath))
        {
            var failureReason = _cpuRuntimeManager.FailureReason
                ?? $"Expected managed CPU Python at {PythonPath}.";
            throw new InvalidOperationException(
                $"Managed CPU runtime is not ready for subprocess providers. {failureReason}");
        }
    }

    private static bool IsValidScriptPrefix(string prefix) =>
        !string.IsNullOrEmpty(prefix) &&
        prefix.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');

    private static async Task WriteStandardInputAsync(
        Process process,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        if (standardInput is null)
            return;

        try
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Process likely died or closed stdin — expected in some scenarios
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            // Process was disposed — expected during cancellation
        }
    }

    /// <summary>
    /// Logs and throws <see cref="InvalidOperationException"/> when a script invocation
    /// returned a non-zero exit code.
    /// </summary>
    protected void ThrowIfFailed(ScriptResult result, string operationName)
    {
        if (result.ExitCode != 0)
        {
            Log.Error($"{operationName} failed (exit {result.ExitCode})", new Exception(result.Stderr));
            throw new InvalidOperationException($"{operationName} failed: {result.Stderr}");
        }
    }
}