using System;
using System.Threading.Tasks;

namespace Babel.Player.Services;

internal static class TaskExtensions
{
    /// <summary>
    /// Safely executes a task in the background without awaiting it.
    /// Any unhandled exceptions are caught and explicitly routed to the application log.
    /// </summary>
    /// <param name="task">The task to run in the background.</param>
    /// <param name="log">Logger used to record unhandled exceptions from the task.</param>
    /// <param name="context">A short description of the task included in the log message; defaults to "background operation".</param>
    /// <returns>The original task for optional observation or chaining.</returns>
    /// <summary>
    /// Starts the specified task without awaiting it and routes any unhandled exceptions to the provided logger.
    /// </summary>
    /// <param name="task">The background task to run.</param>
    /// <param name="log">Logger used to record unhandled exceptions.</param>
    /// <param name="context">Context string included in the log message; defaults to "background operation".</param>
    /// <returns>The same <see cref="Task"/> instance passed in.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="task"/> or <paramref name="log"/> is null.</exception>
    public static Task FireAndForgetAsync(this Task task, AppLog log, string context = "background operation")
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(log);

        _ = ObserveFaultAsync(task, log, context);

        return task;
    }

    private static async Task ObserveFaultAsync(Task task, AppLog log, string context)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Canceled work is intentionally silent.
        }
        catch (Exception ex)
        {
            log.Error($"Unhandled exception during {context}", ex);
        }
    }
}
