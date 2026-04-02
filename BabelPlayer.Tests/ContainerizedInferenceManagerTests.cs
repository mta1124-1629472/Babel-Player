using System;
using System.Collections.Generic;
using System.IO;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class ContainerizedInferenceManagerTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;

    public ContainerizedInferenceManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-container-manager-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public void EnsureAvailable_WhenServiceAlreadyHealthy_DoesNotStartDocker()
    {
        var invocations = new List<ContainerizedInferenceManager.ProcessInvocation>();
        var manager = new ContainerizedInferenceManager(
            _log,
            "http://localhost:8000",
            _dir,
            (url, timeoutSeconds) => new ContainerHealthStatus(true, false, null, url, null),
            invocation =>
            {
                invocations.Add(invocation);
                return new ContainerizedInferenceManager.ProcessInvocationResult(0, "", "");
            });

        var health = manager.EnsureAvailable();

        Assert.True(health.IsAvailable);
        Assert.False(manager.StartedByManager);
        Assert.Empty(invocations);
    }

    [Fact]
    public void EnsureAvailable_WhenStartupSucceeds_MarksServiceAsManaged()
    {
        var invocations = new List<ContainerizedInferenceManager.ProcessInvocation>();
        var probeCount = 0;
        var manager = new ContainerizedInferenceManager(
            _log,
            "http://localhost:8000",
            _dir,
            (url, timeoutSeconds) =>
            {
                probeCount++;
                return probeCount >= 3
                    ? new ContainerHealthStatus(true, true, "12.4", url, null)
                    : ContainerHealthStatus.Unavailable(url, "connection refused");
            },
            invocation =>
            {
                invocations.Add(invocation);
                return new ContainerizedInferenceManager.ProcessInvocationResult(0, "started", "");
            });

        var health = manager.EnsureAvailable(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(1));

        Assert.True(health.IsAvailable);
        Assert.True(manager.StartedByManager);
        Assert.Single(invocations);
        Assert.Contains("compose up -d inference", invocations[0].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void StopManagedService_WhenManagerStartedService_RunsDockerStop()
    {
        var invocations = new List<ContainerizedInferenceManager.ProcessInvocation>();
        var probeCount = 0;
        var manager = new ContainerizedInferenceManager(
            _log,
            "http://localhost:8000",
            _dir,
            (url, timeoutSeconds) =>
            {
                probeCount++;
                return probeCount >= 2
                    ? new ContainerHealthStatus(true, false, null, url, null)
                    : ContainerHealthStatus.Unavailable(url, "down");
            },
            invocation =>
            {
                invocations.Add(invocation);
                return new ContainerizedInferenceManager.ProcessInvocationResult(0, "", "");
            });

        manager.EnsureAvailable(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1));
        manager.StopManagedService();

        Assert.Equal(2, invocations.Count);
        Assert.Contains("compose up -d inference", invocations[0].Arguments, StringComparison.Ordinal);
        Assert.Contains("compose stop inference", invocations[1].Arguments, StringComparison.Ordinal);
        Assert.False(manager.StartedByManager);
    }
}
