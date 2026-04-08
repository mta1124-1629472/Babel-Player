using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

[Collection("PythonSubprocessService")]
public sealed class PythonSubprocessServiceBaseTests : IDisposable
{
    private readonly string _tempLogPath;

    public PythonSubprocessServiceBaseTests()
    {
        _tempLogPath = Path.GetTempFileName();
    }

    public void Dispose()
    {
        if (File.Exists(_tempLogPath))
            File.Delete(_tempLogPath);
    }

    private class TestPythonService(AppLog log) : PythonSubprocessServiceBase(log)
    {
        public Task<ScriptResult> RunTestScriptAsync(
            string scriptContent,
            CancellationToken cancellationToken = default)
        {
            return RunPythonScriptAsync(
                scriptContent,
                scriptPrefix: "test",
                cancellationToken: cancellationToken);
        }
        
        public Task<ScriptResult> RunTestScriptAsync(
            string scriptContent,
            string? standardInput,
            CancellationToken cancellationToken = default)
        {
            return RunPythonScriptAsync(
                scriptContent,
                scriptPrefix: "test",
                standardInput: standardInput,
                cancellationToken: cancellationToken);
        }
        
        public Task<ScriptResult> RunTestScriptAsync(
            string scriptContent,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            return RunPythonScriptAsync(
                scriptContent,
                arguments: arguments,
                scriptPrefix: "test",
                cancellationToken: cancellationToken);
        }
        
        public Task<ScriptResult> RunTestScriptAsync(
            string scriptContent,
            IReadOnlyDictionary<string, string> environmentVariables,
            CancellationToken cancellationToken = default)
        {
            return RunPythonScriptAsync(
                scriptContent,
                scriptPrefix: "test",
                environmentVariables: environmentVariables,
                cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public async Task RunPythonScriptAsync_HandlesCancellationGracefully()
    {
        if (DependencyLocator.FindPython() is null) return;
        using var log = new AppLog(_tempLogPath);
        var service = new TestPythonService(log);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Script that runs longer than cancellation timeout
        var longRunningScript = @"
import time
time.sleep(2)
print('Should not reach here')
";

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.RunTestScriptAsync(longRunningScript, cts.Token);
        });

        Assert.Equal(cts.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task RunPythonScriptAsync_CleansUpTempFileOnCancellation()
    {
        if (DependencyLocator.FindPython() is null) return;
        using var log = new AppLog(_tempLogPath);
        var service = new TestPythonService(log);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var script = @"
import time
time.sleep(2)
";

        var tempDirectory = Path.GetTempPath();
        var existingTempFiles = Directory.GetFiles(tempDirectory, "test_*.py");
        string? createdTempFile = null;

        var runTask = service.RunTestScriptAsync(script, cts.Token);

        for (var attempt = 0; attempt < 20 && createdTempFile is null && !runTask.IsCompleted; attempt++)
        {
            foreach (var tempFile in Directory.GetFiles(tempDirectory, "test_*.py"))
            {
                var isExistingFile = false;
                foreach (var existingTempFile in existingTempFiles)
                {
                    if (string.Equals(existingTempFile, tempFile, StringComparison.Ordinal))
                    {
                        isExistingFile = true;
                        break;
                    }
                }

                if (!isExistingFile)
                {
                    createdTempFile = tempFile;
                    break;
                }
            }

            if (createdTempFile is null)
            {
                await Task.Delay(25);
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await runTask;
        });

        if (createdTempFile is not null)
        {
            await Task.Delay(1000); // Allow time for cleanup to complete
            Assert.False(File.Exists(createdTempFile), $"Expected temp file '{createdTempFile}' to be deleted.");
        }
    }

    [Fact]
    public async Task RunPythonScriptAsync_HandlesStdinProcessDeath()
    {
        if (DependencyLocator.FindPython() is null) return;
        using var log = new AppLog(_tempLogPath);
        var service = new TestPythonService(log);

        // Script that exits immediately without reading stdin
        var script = @"
import sys
sys.exit(1)
";

        var result = await service.RunTestScriptAsync(
            script,
            standardInput: "This input should not cause issues",
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task RunPythonScriptAsync_HandlesStdinIOException()
    {
        if (DependencyLocator.FindPython() is null) return;
        using var log = new AppLog(_tempLogPath);
        var service = new TestPythonService(log);

        // Script that closes stdin immediately
        var script = @"
import sys
sys.stdin.close()
print('Stdin closed successfully')
";

        var result = await service.RunTestScriptAsync(
            script,
            standardInput: "This input will fail",
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Stdin closed successfully", result.Stdout);
    }

    [Fact]
    public async Task RunPythonScriptAsync_PropagatesOperationCancelledException()
    {
        if (DependencyLocator.FindPython() is null) return;
        using var log = new AppLog(_tempLogPath);
        var service = new TestPythonService(log);
        using var cts = new CancellationTokenSource();

        // Cancel before starting the script
        cts.Cancel();

        var script = "print('Hello')";

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.RunTestScriptAsync(script, cts.Token);
        });

        Assert.Equal(cts.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task RunPythonScriptAsync_HandlesProcessKillFailure()
    {
        // Skip on Windows - SIGTERM doesn't exist on Windows Python
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (DependencyLocator.FindPython() is null) return;
        using var log = new AppLog(_tempLogPath);
        var service = new TestPythonService(log);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Script that might resist termination (though most Python processes will terminate)
        var script = @"
import time
import signal
def handler(signum, frame):
    pass  # Ignore SIGTERM
signal.signal(signal.SIGTERM, handler)

# This will still eventually be killed by the entireProcessTree: true
time.sleep(10)
";

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.RunTestScriptAsync(script, cts.Token);
        });

        Assert.Equal(cts.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task RunPythonScriptAsync_SuccessfulExecutionWithCancellation()
    {
        if (DependencyLocator.FindPython() is null) return;
        using var log = new AppLog(_tempLogPath);
        var service = new TestPythonService(log);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var script = @"
print('Test successful execution')
print('With multiple lines')
";

        var result = await service.RunTestScriptAsync(script, cts.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Test successful execution", result.Stdout);
        Assert.Contains("With multiple lines", result.Stdout);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task RunPythonScriptAsync_HandlesEnvironmentVariables()
    {
        if (DependencyLocator.FindPython() is null) return;
        using var log = new AppLog(_tempLogPath);
        var service = new TestPythonService(log);

        var script = @"
import os
print('TEST_VAR=' + os.environ.get('TEST_VAR', 'not_set'))
print('ANOTHER_VAR=' + os.environ.get('ANOTHER_VAR', 'not_set'))
";

        var envVars = new Dictionary<string, string>
        {
            ["TEST_VAR"] = "test_value",
            ["ANOTHER_VAR"] = "another_value"
        };

        var result = await service.RunTestScriptAsync(
            script,
            environmentVariables: envVars,
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("TEST_VAR=test_value", result.Stdout);
        Assert.Contains("ANOTHER_VAR=another_value", result.Stdout);
    }

    [Fact]
    public async Task RunPythonScriptAsync_HandlesArguments()
    {
        if (DependencyLocator.FindPython() is null) return;
        using var log = new AppLog(_tempLogPath);
        var service = new TestPythonService(log);

        var script = @"
import sys
print('Arg count: ' + str(len(sys.argv)))
for i, arg in enumerate(sys.argv):
    print('Arg ' + str(i) + ': ' + arg)
";

        var args = new[] { "arg1", "arg2", "arg with spaces" };

        var result = await service.RunTestScriptAsync(
            script,
            arguments: args,
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Arg count: 4", result.Stdout); // script name + 3 args
        Assert.Contains("Arg 1: arg1", result.Stdout);
        Assert.Contains("Arg 2: arg2", result.Stdout);
        Assert.Contains("Arg 3: arg with spaces", result.Stdout);
    }
}
