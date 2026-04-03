using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class AppLog : IDisposable, IAsyncDisposable
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    private const int MaxArchivedFiles = 4; // keep 4 archives + 1 current = 5 total

    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
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

    private async Task BackgroundWriterAsync()
    {
        var reader = _channel.Reader;
        try
        {
            await foreach (var line in reader.ReadAllAsync(_cts.Token))
            {
                try { await File.AppendAllTextAsync(LogFilePath, line); }
                catch { /* best-effort: log writes are never fatal */ }
            }
        }
        catch (OperationCanceledException) { }

        // Drain remaining entries after cancellation.
        while (reader.TryRead(out var remaining))
        {
            try { File.AppendAllText(LogFilePath, remaining); }
            catch { }
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { _writerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await _writerTask.ConfigureAwait(false); } catch { }
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
