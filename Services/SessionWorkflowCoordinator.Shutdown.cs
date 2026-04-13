using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ConcurrentDictionary<int, Task> _ownedBackgroundOperations = new();
    private int _ownedBackgroundOperationId;
    private int _shutdownRequested;

    public void StartStartupWarmupTasks(
        Action<BootstrapWarmupData> applyBootstrapWarmup,
        Action<HardwareSnapshot> applyHardwareSnapshot)
    {
        ArgumentNullException.ThrowIfNull(applyBootstrapWarmup);
        ArgumentNullException.ThrowIfNull(applyHardwareSnapshot);

        QueueOwnedBackgroundOperation(
            "bootstrap warmup",
            async cancellationToken =>
            {
                var warmup = await Task.Run(GatherBootstrapWarmupData, cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                    applyBootstrapWarmup(warmup);
            });

        QueueOwnedBackgroundOperation(
            "hardware detection",
            async cancellationToken =>
            {
                var hardware = await Task.Run(() => global::Babel.Player.Services.HardwareSnapshot.Run(), cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                    applyHardwareSnapshot(hardware);
            });
    }

    private void QueueOwnedBackgroundOperation(
        string operationName,
        Func<CancellationToken, Task> operation)
    {
        if (_shutdownCts.IsCancellationRequested)
            return;

        var operationId = Interlocked.Increment(ref _ownedBackgroundOperationId);
        Task task = Task.Run(
            async () =>
            {
                try
                {
                    await operation(_shutdownCts.Token);
                }
                catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
                {
                    _log.Info($"Background operation canceled during shutdown: {operationName}");
                }
                catch (Exception ex)
                {
                    _log.Error($"Background operation failed: {operationName}", ex);
                }
                finally
                {
                    _ownedBackgroundOperations.TryRemove(operationId, out _);
                }
            },
            CancellationToken.None);

        _ownedBackgroundOperations[operationId] = task;
    }

    private void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 1)
            return;

        _shutdownCts.Cancel();
    }

    private void WaitForOwnedBackgroundOperations(TimeSpan timeout)
    {
        var tasks = _ownedBackgroundOperations.Values.ToArray();
        if (tasks.Length == 0)
            return;

        try
        {
            if (!Task.WhenAll(tasks).Wait(timeout))
                _log.Warning($"Shutdown timed out waiting for {tasks.Length} background coordinator task(s).");
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed while waiting for coordinator background tasks during shutdown: {ex.Message}");
        }
    }
}
