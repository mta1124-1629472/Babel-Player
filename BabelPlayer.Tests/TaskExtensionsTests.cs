using System;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for <see cref="TaskExtensions.FireAndForgetAsync"/>.
/// </summary>
public sealed class TaskExtensionsTests : IDisposable
{
    private readonly string _testDir;
    private readonly AppLog _log;

    public TaskExtensionsTests()
    {
        _testDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"babel-taskext-tests-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(_testDir);
        _log = new AppLog(System.IO.Path.Combine(_testDir, "test.log"));
    }

    public void Dispose()
    {
        try { _log.Dispose(); }
        catch { }
        try { System.IO.Directory.Delete(_testDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── Null argument checks ───────────────────────────────────────────────────

    [Fact]
    public async Task FireAndForgetAsync_NullTask_ThrowsArgumentNullException()
    {
        Task? nullTask = null;
        await Assert.ThrowsAsync<ArgumentNullException>(() => nullTask!.FireAndForgetAsync(_log));
    }

    [Fact]
    public async Task FireAndForgetAsync_NullLog_ThrowsArgumentNullException()
    {
        var task = Task.CompletedTask;
        await Assert.ThrowsAsync<ArgumentNullException>(() => task.FireAndForgetAsync(null!));
    }

    // ── Return value ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FireAndForgetAsync_ReturnsOriginalTask()
    {
        var tcs = new TaskCompletionSource();
        var originalTask = tcs.Task;

        var returnedTask = originalTask.FireAndForgetAsync(_log);

        // The returned task should be the same instance
        Assert.Same(originalTask, returnedTask);

        // Complete the original task
        tcs.SetResult();
        await returnedTask;
    }

    [Fact]
    public void FireAndForgetAsync_CompletedTask_ReturnsAlreadyCompletedTask()
    {
        var result = Task.CompletedTask.FireAndForgetAsync(_log);
        Assert.True(result.IsCompleted);
    }

    // ── Exception handling ─────────────────────────────────────────────────────

    [Fact]
    public async Task FireAndForgetAsync_FaultedTask_LogsError()
    {
        var logPath = System.IO.Path.Combine(_testDir, "fault-test.log");
        {
            using var log = new AppLog(logPath);

            var faultedTask = Task.FromException(new InvalidOperationException("test error"));
            _ = faultedTask.FireAndForgetAsync(log, "test operation");

            // Allow the continuation to run
            await Task.Delay(100);
            await log.FlushAsync();
        }

        var logContent = await System.IO.File.ReadAllTextAsync(logPath);
        Assert.Contains("ERROR", logContent);
        Assert.Contains("test operation", logContent);
    }

    [Fact]
    public async Task FireAndForgetAsync_SuccessfulTask_DoesNotLogError()
    {
        var logPath = System.IO.Path.Combine(_testDir, "success-test.log");
        {
            using var log = new AppLog(logPath);

            var successfulTask = Task.CompletedTask;
            _ = successfulTask.FireAndForgetAsync(log, "success operation");

            await Task.Delay(50);
            await log.FlushAsync();
        }

        // Log file may not exist or be empty if no entries were written
        if (System.IO.File.Exists(logPath))
        {
            var logContent = await System.IO.File.ReadAllTextAsync(logPath);
            Assert.DoesNotContain("ERROR", logContent);
        }
    }

    [Fact]
    public async Task FireAndForgetAsync_FaultedTask_LogsExceptionContext()
    {
        var logPath = System.IO.Path.Combine(_testDir, "context-test.log");
        {
            using var log = new AppLog(logPath);

            var ex = new ArgumentException("invalid argument");
            var faultedTask = Task.FromException(ex);
            _ = faultedTask.FireAndForgetAsync(log, "my custom context");

            await Task.Delay(100);
            await log.FlushAsync();
        }

        var logContent = await System.IO.File.ReadAllTextAsync(logPath);
        Assert.Contains("my custom context", logContent);
    }

    // ── Default context ────────────────────────────────────────────────────────

    [Fact]
    public async Task FireAndForgetAsync_DefaultContext_UsedInLogMessage()
    {
        var logPath = System.IO.Path.Combine(_testDir, "default-ctx-test.log");
        {
            using var log = new AppLog(logPath);

            var faultedTask = Task.FromException(new Exception("boom"));
            _ = faultedTask.FireAndForgetAsync(log);  // no context argument

            await Task.Delay(100);
            await log.FlushAsync();
        }

        var logContent = await System.IO.File.ReadAllTextAsync(logPath);
        Assert.Contains("background operation", logContent);
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FireAndForgetAsync_CanceledTask_DoesNotLogError()
    {
        var logPath = System.IO.Path.Combine(_testDir, "canceled-test.log");
        {
            using var log = new AppLog(logPath);

            var canceledTask = Task.FromCanceled(new CancellationToken(canceled: true));
            _ = canceledTask.FireAndForgetAsync(log, "canceled op");

            await Task.Delay(50);
            await log.FlushAsync();
        }

        // Canceled tasks are not faulted, so no error should be logged
        if (System.IO.File.Exists(logPath))
        {
            var logContent = await System.IO.File.ReadAllTextAsync(logPath);
            Assert.DoesNotContain("ERROR", logContent);
        }
    }

    // ── Async task faults ─────────────────────────────────────────────────────

    [Fact]
    public async Task FireAndForgetAsync_TaskFaultsAfterDelay_LogsError()
    {
        var logPath = System.IO.Path.Combine(_testDir, "delayed-fault-test.log");
        {
            using var log = new AppLog(logPath);

            async Task FailAfterDelay()
            {
                await Task.Delay(20);
                throw new InvalidOperationException("delayed failure");
            }

            var task = FailAfterDelay();
            _ = task.FireAndForgetAsync(log, "delayed operation");

            // Wait for the task to fault and continuation to run
            await Task.Delay(200);
            await log.FlushAsync();
        }

        var logContent = await System.IO.File.ReadAllTextAsync(logPath);
        Assert.Contains("ERROR", logContent);
        Assert.Contains("delayed operation", logContent);
    }
}