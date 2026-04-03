using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class ContainerizedServiceProbeTests
{
    [Fact]
    public async Task GetCurrentOrStartBackgroundProbe_ReusesCachedAvailableResult()
    {
        var callCount = 0;
        var log = new AppLog(Path.GetTempFileName());

        var probe = new ContainerizedServiceProbe(log, async (url, _, _) =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Delay(25);
            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: false,
                CudaVersion: null,
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null));
        });

        var first = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
        Assert.Equal(ContainerizedProbeState.Checking, first.State);

        await Task.Delay(100);

        var second = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
        Assert.Equal(ContainerizedProbeState.Available, second.State);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task WaitForProbeAsync_ReturnsCheckingWhenBudgetExpires()
    {
        var log = new AppLog(Path.GetTempFileName());

        var probe = new ContainerizedServiceProbe(log, async (url, _, _) =>
        {
            await Task.Delay(250);
            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: false,
                CudaVersion: null,
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null));
        });

        var result = await probe.WaitForProbeAsync(
            "http://localhost:8000",
            forceRefresh: true,
            waitTimeout: TimeSpan.FromMilliseconds(20));

        Assert.Equal(ContainerizedProbeState.Checking, result.State);
    }

    [Fact]
    public async Task WaitForProbeAsync_RetriesUntilServiceBecomesAvailableWithinBudget()
    {
        var callCount = 0;
        var log = new AppLog(Path.GetTempFileName());

        var probe = new ContainerizedServiceProbe(log, (url, _, _) =>
        {
            var currentCall = Interlocked.Increment(ref callCount);
            var health = currentCall < 3
                ? ContainerHealthStatus.Unavailable(url, $"connection refused #{currentCall}")
                : new ContainerHealthStatus(
                    IsAvailable: true,
                    CudaAvailable: true,
                    CudaVersion: "12.8",
                    ServiceUrl: url,
                    ErrorMessage: null,
                    Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null));
            return Task.FromResult(health);
        });

        var result = await probe.WaitForProbeAsync(
            "http://localhost:8000",
            forceRefresh: true,
            waitTimeout: TimeSpan.FromSeconds(1));

        Assert.Equal(ContainerizedProbeState.Available, result.State);
        Assert.True(callCount >= 3);
    }

    [Fact]
    public async Task WaitForProbeAsync_ReturnsLastUnavailableWhenBudgetExpires()
    {
        var callCount = 0;
        var log = new AppLog(Path.GetTempFileName());

        var probe = new ContainerizedServiceProbe(log, (url, _, _) =>
        {
            var currentCall = Interlocked.Increment(ref callCount);
            return Task.FromResult(ContainerHealthStatus.Unavailable(url, $"connection refused #{currentCall}"));
        });

        var result = await probe.WaitForProbeAsync(
            "http://localhost:8000",
            forceRefresh: true,
            waitTimeout: TimeSpan.FromMilliseconds(650));

        Assert.Equal(ContainerizedProbeState.Unavailable, result.State);
        Assert.Contains("connection refused", result.ErrorDetail, StringComparison.OrdinalIgnoreCase);
        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task WaitForProbeAsync_ForceRefreshBypassesCache()
    {
        var callCount = 0;
        var log = new AppLog(Path.GetTempFileName());

        var probe = new ContainerizedServiceProbe(log, (url, _, _) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: false,
                CudaVersion: null,
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null)));
        });

        var first = await probe.WaitForProbeAsync("http://localhost:8000", forceRefresh: false, waitTimeout: TimeSpan.FromSeconds(1));
        var second = await probe.WaitForProbeAsync("http://localhost:8000", forceRefresh: false, waitTimeout: TimeSpan.FromSeconds(1));
        var third = await probe.WaitForProbeAsync("http://localhost:8000", forceRefresh: true, waitTimeout: TimeSpan.FromSeconds(1));

        Assert.Equal(ContainerizedProbeState.Available, first.State);
        Assert.Equal(ContainerizedProbeState.Available, second.State);
        Assert.Equal(ContainerizedProbeState.Available, third.State);
        Assert.Equal(2, callCount);
    }
}
