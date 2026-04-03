using System;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

internal static class BackgroundTaskObserver
{
    public static void Observe(Task task, AppLog log, string operationName)
    {
        task.ContinueWith(
            t =>
            {
                if (t.Exception is null)
                    return;

                log.Error(
                    $"{operationName} failed unexpectedly.",
                    t.Exception.Flatten());
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
