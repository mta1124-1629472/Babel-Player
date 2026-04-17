using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class AppLog : IDisposable, IAsyncDisposable
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    private const int MaxArchivedFiles = 4; // keep 4 archives + 1 current = 5 total

    private readonly Channel<object> _channel = Channel.CreateUnbounded<object>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts = new();

    public AppLog(string logFilePath)
    {
        LogFilePath = logFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
        RotateIfNeeded();
        _writerTask = Task.Run(BackgroundWriterAsync);
    }

    public string LogFilePath { get; }

    public void Info(string message)    => Write("INFO",  message);
    public void Warning(string message) => Write("WARN",  message);

    public void Error(string message, Exception exception)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {message}{Environment.NewLine}";
        _channel.Writer.TryWrite(line);
    }

    /// <summary>
    /// Waits until all log lines enqueued before this call have been written to disk.
    /// Safe to call after <see cref="Dispose"/>; returns immediately if the channel is closed.
    /// </summary>
    public async Task FlushAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(tcs))
            return; // channel already closed — nothing left to flush
        await tcs.Task.ConfigureAwait(false);
    }

    private const int MaxBatchSize = 50;

    private async Task BackgroundWriterAsync()
    {
        var reader = _channel.Reader;
        var batch = new StringBuilder();
        var flushSignals = new List<TaskCompletionSource<bool>>();

        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                // Drain up to MaxBatchSize items in one pass so we can issue a single I/O call.
                while (batch.Length < MaxBatchSize * 256 // soft cap on batched bytes
                    && flushSignals.Count < MaxBatchSize
                    && reader.TryRead(out var item))
                {
                    if (item is string line)
                    {
                        batch.Append(line);
                    }
                    else if (item is TaskCompletionSource<bool> tcs)
                    {
                        flushSignals.Add(tcs);
                    }
                }

                await WriteBatchAsync(batch, flushSignals).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }

        // Drain remaining entries after cancellation (best effort, synchronous is fine during shutdown).
        while (reader.TryRead(out var remaining))
        {
            if (remaining is string line)
            {
                batch.Append(line);
            }
            else if (remaining is TaskCompletionSource<bool> tcs)
            {
                flushSignals.Add(tcs);
            }
        }

        if (batch.Length > 0)
        {
            try { File.AppendAllText(LogFilePath, batch.ToString()); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to drain log batch to '{LogFilePath}': {ex}"); }
            batch.Clear();
        }

        foreach (var tcs in flushSignals)
        {
            tcs.TrySetResult(true);
        }
        flushSignals.Clear();
    }

    private async Task WriteBatchAsync(StringBuilder batch, List<TaskCompletionSource<bool>> flushSignals)
    {
        if (batch.Length > 0)
        {
            try { await File.AppendAllTextAsync(LogFilePath, batch.ToString()).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to write log batch to '{LogFilePath}': {ex}"); }
            batch.Clear();
        }

        if (flushSignals.Count > 0)
        {
            foreach (var tcs in flushSignals)
            {
                tcs.TrySetResult(true);
            }
            flushSignals.Clear();
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Log or rethrow non-cancellation exceptions
            try { File.AppendAllText(LogFilePath, $"{DateTimeOffset.UtcNow:O} [ERROR] Dispose failed: {ex}{Environment.NewLine}"); }
            catch { }
        }
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Log or rethrow non-cancellation exceptions
            try { await File.AppendAllTextAsync(LogFilePath, $"{DateTimeOffset.UtcNow:O} [ERROR] DisposeAsync failed: {ex}{Environment.NewLine}"); }
            catch { }
        }
        _cts.Dispose();
    }

    /// <summary>
    /// If the current log file exceeds <see cref="MaxFileSizeBytes"/>, renames it to a
    /// timestamped archive and prunes any archives beyond <see cref="MaxArchivedFiles"/>.
    /// Called once at startup — no in-process rotation needed for typical session lengths.
    /// </summary>
    private void RotateIfNeeded()
    {
        if (!File.Exists(LogFilePath))
            return;

        if (new FileInfo(LogFilePath).Length < MaxFileSizeBytes)
            return;

        var dir       = Path.GetDirectoryName(LogFilePath)!;
        var stem      = Path.GetFileNameWithoutExtension(LogFilePath);
        var ext       = Path.GetExtension(LogFilePath);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var archive   = Path.Combine(dir, $"{stem}.{timestamp}{ext}");

        File.Move(LogFilePath, archive);

        // Prune oldest archives, keeping at most MaxArchivedFiles
        var archives = Directory.GetFiles(dir, $"{stem}.*{ext}")
            .Where(f => !string.Equals(f, LogFilePath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f)
            .ToArray();

        foreach (var old in archives.Skip(MaxArchivedFiles))
        {
            try { File.Delete(old); }
            catch { /* best-effort */ }
        }
    }
}