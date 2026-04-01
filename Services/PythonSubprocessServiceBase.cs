using System;
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
/// Derived classes provide the script content and call-specific argument strings;
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
    protected sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Writes <paramref name="scriptContent"/> to a uniquely-named temp file, runs it
    /// under <see cref="PythonPath"/> with <paramref name="arguments"/> appended after
    /// the script path, captures stdout/stderr, then deletes the temp file.
    ///
    /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/>
    /// is signalled during the wait.
    /// </summary>
    /// <param name="scriptContent">Python source to execute.</param>
    /// <param name="arguments">
    /// Space-separated arguments that follow the script path on the command line.
    /// Callers are responsible for quoting individual paths/values.
    /// </param>
    /// <param name="scriptPrefix">
    /// Short label used in the temp filename (e.g. "transcribe", "translate_seg").
    /// Defaults to "script".
    /// </param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    protected async Task<ScriptResult> RunPythonScriptAsync(
        string scriptContent,
        string arguments,
        string scriptPrefix = "script",
        CancellationToken cancellationToken = default)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{scriptPrefix}_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, scriptContent, cancellationToken);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName              = PythonPath,
                Arguments             = $"\"{scriptPath}\" {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start Python process ({scriptPrefix}).");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(cancellationToken);
            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;

            return new ScriptResult(proc.ExitCode, stdout, stderr);
        }
        finally
        {
            if (File.Exists(scriptPath))
                File.Delete(scriptPath);
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
