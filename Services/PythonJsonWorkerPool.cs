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
            const int workerShutdownTimeoutMs = 2000;
            const int stderrReadTimeoutMs = 2000;

            // If the process hasn't exited, kill it before reading stderr to avoid waiting indefinitely
            // for the worker to terminate and close its stderr pipe.
            var killAttempted = false;
            if (!worker.Process.HasExited)
            {
                killAttempted = true;
                try
                {
                    worker.Process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort.
                }
            }

            try
            {
                await worker.Process.WaitForExitAsync(linkedCts.Token)
                    .WaitAsync(TimeSpan.FromMilliseconds(workerShutdownTimeoutMs), linkedCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                // Best effort: continue and bound stderr reading below.
            }
            catch
            {
                // Best effort.
            }

            string stderr;
            try
            {
                using var stderrTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(stderrReadTimeoutMs));
                using var linkedStderrCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, stderrTimeoutCts.Token);
                stderr = await ReadBoundedStderrAsync(worker.Process.StandardError, linkedStderrCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                // External cancellation: propagate so callers can observe it.
                throw;
            }
            catch (OperationCanceledException)
            {
                // Internal timeout: stderr read took too long; treat as empty.
                stderr = "Timed out while reading worker stderr.";
            }
            catch (Exception ex)
            {
                stderr = $"Failed to read worker stderr: {ex.Message}";
            }

            var killSuffix = killAttempted ? " (kill attempted)." : ".";
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

    /// <summary>
    /// Reads stderr from a worker process up to a fixed line and character budget.
    /// Using ReadToEndAsync risks large allocations and poor cancellation granularity
    /// when the process has dumped substantial output (e.g. model crash traceback) before
    /// dying. Reading line-by-line lets the cancellation token fire between reads, and
    /// discarding lines beyond the cap bounds worst-case allocation.
    /// </summary>
    private static async Task<string> ReadBoundedStderrAsync(
        StreamReader stderr,
        CancellationToken cancellationToken,
        int maxLines = 50,
        int maxTotalChars = 4096)
    {
        var builder = new System.Text.StringBuilder();
        var linesRead = 0;
        var truncated = false;

        while (linesRead < maxLines && builder.Length < maxTotalChars)
        {
            var line = await stderr.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;

            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(line);
            linesRead++;
        }

        // Drain the pipe without capturing, so the OS pipe buffer doesn't
        // block the process from exiting. The cancellation token keeps this bounded.
        string? nextLine = await stderr.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (nextLine is not null)
        {
            truncated = true;
            while (await stderr.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                // discard
            }
        }

        if (truncated)
            builder.Append($"\n[... stderr truncated after {linesRead} lines ...]");

        return builder.ToString();
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
        catch (Exception ex)
        {
            _log.Info($"Failed to kill {_poolName} worker {worker.Index + 1}: {ex.Message}");
        }

        try
        {
            worker.Process.Dispose();
        }
        catch (Exception ex)
        {
            _log.Info($"Failed to dispose {_poolName} worker {worker.Index + 1}: {ex.Message}");
        }

        _log.Info($"Disposed {_poolName} worker {worker.Index + 1}: {reason}.");
    }
}