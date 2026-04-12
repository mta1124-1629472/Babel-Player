using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Babel.Player.Services;

internal sealed class PythonJsonWorkerPool<TRequest, TResponse> : IDisposable
    where TRequest : class
    where TResponse : class
{
    private readonly record struct WorkItem(
        string RequestId,
        TRequest Request,
        TaskCompletionSource<TResponse> Completion,
        CancellationToken CancellationToken);

    private sealed class WorkerState(int index, Process process)
    {
        public int Index { get; } = index;
        public Process Process { get; } = process;
    }

    private sealed class WorkerRequestEnvelope<TPayload>
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("payload")]
        public required TPayload Payload { get; init; }
    }

    private sealed class WorkerResponseEnvelope<TPayload>
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("payload")]
        public TPayload? Payload { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly AppLog _log;
    private readonly string _poolName;
    private readonly string _pythonPath;
    private readonly string _scriptPath;
    private readonly IReadOnlyList<string> _scriptArguments;
    private readonly Func<CancellationToken, Task> _ensureRuntimeReadyAsync;
    private readonly Channel<WorkItem> _queue;
    private readonly List<Task> _workerTasks = [];
    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposed;

    public PythonJsonWorkerPool(
        AppLog log,
        string poolName,
        string pythonPath,
        string scriptPath,
        int workerCount,
        Func<CancellationToken, Task> ensureRuntimeReadyAsync,
        IReadOnlyList<string>? scriptArguments = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(poolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentNullException.ThrowIfNull(ensureRuntimeReadyAsync);

        _log = log;
        _poolName = poolName;
        _pythonPath = pythonPath;
        _scriptPath = scriptPath;
        _scriptArguments = scriptArguments ?? [];
        _ensureRuntimeReadyAsync = ensureRuntimeReadyAsync;
        _queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        for (var index = 0; index < workerCount; index++)
            _workerTasks.Add(Task.Run(() => WorkerLoopAsync(index, _disposeCts.Token), CancellationToken.None));
    }

    public Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workItem = new WorkItem(
            Guid.NewGuid().ToString("N"),
            request,
            completion,
            cancellationToken);

        if (!_queue.Writer.TryWrite(workItem))
            throw new InvalidOperationException($"{_poolName} worker queue is not accepting new work.");

        return completion.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _queue.Writer.TryComplete();
        _disposeCts.Cancel();

        try
        {
            Task.WaitAll([.. _workerTasks], TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best effort during shutdown.
        }

        while (_queue.Reader.TryRead(out var workItem))
            workItem.Completion.TrySetCanceled(_disposeCts.Token);

        _disposeCts.Dispose();
    }

    private async Task WorkerLoopAsync(int workerIndex, CancellationToken cancellationToken)
    {
        WorkerState? worker = null;

        try
        {
            await foreach (var workItem in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                if (workItem.CancellationToken.IsCancellationRequested)
                {
                    workItem.Completion.TrySetCanceled(workItem.CancellationToken);
                    continue;
                }

                try
                {
                    worker ??= await StartWorkerAsync(workerIndex, cancellationToken).ConfigureAwait(false);
                    await ProcessWorkItemAsync(worker, workItem, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (workItem.CancellationToken.IsCancellationRequested)
                {
                    DisposeWorker(worker, $"request {workItem.RequestId} canceled");
                    worker = null;
                    workItem.Completion.TrySetCanceled(workItem.CancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    DisposeWorker(worker, "pool shutting down");
                    worker = null;
                    workItem.Completion.TrySetCanceled(cancellationToken);
                    break;
                }
                catch (Exception ex)
                {
                    DisposeWorker(worker, $"request {workItem.RequestId} failed");
                    worker = null;
                    workItem.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            DisposeWorker(worker, "worker loop exit");
        }
    }

    private async Task<WorkerState> StartWorkerAsync(int workerIndex, CancellationToken cancellationToken)
    {
        await _ensureRuntimeReadyAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(_scriptPath))
            throw new FileNotFoundException($"Python worker script not found: {_scriptPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(_scriptPath);
        foreach (var argument in _scriptArguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {_poolName} worker process.");
        _log.Info($"Started {_poolName} worker {workerIndex + 1} (pid={process.Id}).");
        return new WorkerState(workerIndex, process);
    }

    private async Task ProcessWorkItemAsync(
        WorkerState worker,
        WorkItem workItem,
        CancellationToken poolCancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            poolCancellationToken,
            workItem.CancellationToken);

        var requestLine = JsonSerializer.Serialize(
            new WorkerRequestEnvelope<TRequest>
            {
                Id = workItem.RequestId,
                Payload = workItem.Request,
            },
            JsonOptions);

        await worker.Process.StandardInput.WriteLineAsync(requestLine).WaitAsync(linkedCts.Token).ConfigureAwait(false);
        await worker.Process.StandardInput.FlushAsync().WaitAsync(linkedCts.Token).ConfigureAwait(false);

        var responseLine = await worker.Process.StandardOutput.ReadLineAsync().WaitAsync(linkedCts.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            // If the process hasn't exited, kill it before reading stderr to prevent ReadToEndAsync from hanging
            if (!worker.Process.HasExited)
            {
                try { worker.Process.Kill(entireProcessTree: true); } catch { /* Best effort */ }
            }

            var stderr = await worker.Process.StandardError.ReadToEndAsync(linkedCts.Token).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"{_poolName} worker {worker.Index + 1} exited without a response. {stderr}".Trim());
        }

        var envelope = JsonSerializer.Deserialize<WorkerResponseEnvelope<TResponse>>(responseLine, JsonOptions)
            ?? throw new InvalidOperationException($"{_poolName} worker returned an empty response envelope.");
        if (!string.Equals(envelope.Id, workItem.RequestId, StringComparison.Ordinal))
            throw new InvalidOperationException($"{_poolName} worker response id mismatch: expected {workItem.RequestId}, got {envelope.Id ?? "<null>"}.");
        if (!envelope.Success)
            throw new InvalidOperationException($"{_poolName} worker failed: {envelope.Error ?? "Unknown worker error."}");
        if (envelope.Payload is null)
            throw new InvalidOperationException($"{_poolName} worker returned success without a payload.");

        workItem.Completion.TrySetResult(envelope.Payload);
    }

    private void DisposeWorker(WorkerState? worker, string reason)
    {
        if (worker is null)
            return;

        try
        {
            if (!worker.Process.HasExited)
                worker.Process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort.
        }

        try
        {
            worker.Process.Dispose();
        }
        catch
        {
            // Best effort.
        }

        _log.Info($"Disposed {_poolName} worker {worker.Index + 1}: {reason}.");
    }
}
