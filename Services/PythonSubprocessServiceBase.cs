using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Base class for services that execute inference work by spawning Python subprocesses.
///
/// Eliminates boilerplate shared by all five AI pipeline services:
///   - Python executable discovery via <see cref="DependencyLocator"/>
///   - Temp-script write → process spawn → stdout/stderr capture → cleanup
///   - Async wait with <see cref="CancellationToken"/> support
///   - Uniform failure logging and exception throwing
///
/// Derived classes provide the script content and call-specific argument values;
/// this class handles the execution lifecycle.
/// </summary>
public abstract class PythonSubprocessServiceBase
{
    protected readonly AppLog Log;
    protected readonly string PythonPath;

    protected PythonSubprocessServiceBase(AppLog log)
    {
        Log        = log;
        PythonPath = DependencyLocator.FindPython()
            ?? throw new InvalidOperationException(
                "Python not found. Expected bundled python next to the app or python on PATH.");
    }

    /// <summary>Captures the output of a single Python subprocess invocation.</summary>
    public sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);

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
    protected async Task<ScriptResult> RunPythonScriptAsync(
        string scriptContent,
        IReadOnlyList<string>? arguments = null,
        string scriptPrefix = "script",
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{scriptPrefix}_{Guid.NewGuid():N}.py");

        // Check for cancellation before doing any work
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await File.WriteAllTextAsync(scriptPath, scriptContent, cancellationToken);

            var psi = new ProcessStartInfo
            {
                FileName              = PythonPath,
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

            var stdinTask = WriteStandardInputAsync(proc, standardInput, cancellationToken);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            
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
                    for (int attempt = 0; attempt < 3 && !terminated; attempt++)
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await proc.WaitForExitAsync(cts.Token);
                            terminated = true;
                        }
                        catch (OperationCanceledException)
                        {
                            // Process didn't terminate within timeout, try again
                            if (attempt < 2)
                            {
                                proc.Kill(entireProcessTree: true); // Try killing again
                            }
                        }
                    }
                    
                    if (!terminated)
                    {
                        // Last resort - try to force terminate without waiting
                        try
                        {
                            proc.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                            // Process is likely already dead or inaccessible
                        }
                    }
                }
                catch 
                { 
                    // Best effort - process might already be dead or inaccessible
                }
                throw;
            }
            
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return new ScriptResult(proc.ExitCode, stdout, stderr);
        }
        finally
        {
            if (File.Exists(scriptPath))
                File.Delete(scriptPath);
        }
    }

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
            // Process likely died or closed stdin - this is expected in some scenarios
            // Don't rethrow - let the main process handling logic detect the failure
        }
        catch (OperationCanceledException)
        {
            // Cancellation requested - let this propagate
            throw;
        }
        catch (ObjectDisposedException)
        {
            // Process was disposed - expected during cancellation
            // Don't rethrow
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
