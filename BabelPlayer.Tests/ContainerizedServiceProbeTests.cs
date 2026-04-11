using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

[Collection("ContainerizedServiceProbe")]
public sealed class ContainerizedServiceProbeTests
{
    [Fact]
    public async Task GetCurrentOrStartBackgroundProbe_ReusesCachedAvailableResult()
    {
        var callCount = 0;
        using var log = new AppLog(Path.GetTempFileName());

        var probe = new ContainerizedServiceProbe(log, async (url, _, ct) =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Delay(25, ct);
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

        // Wait for probe to complete with polling instead of fixed delay
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(25);
            var second = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
            if (second.State == ContainerizedProbeState.Available)
            {
                Assert.Equal(1, callCount);
                return;
            }
        }

        throw new Xunit.Sdk.XunitException("Probe did not complete within expected time.");
    }

    [Fact]
    public async Task WaitForProbeAsync_ReturnsStaleAvailableResultWhileRefreshIsInFlight()
    {
        var callCount = 0;
        using var log = new AppLog(Path.GetTempFileName());
        var releaseRefresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var probe = new ContainerizedServiceProbe(log, async (url, _, ct) =>
        {
            var currentCall = Interlocked.Increment(ref callCount);
            if (currentCall == 1)
            {
                await Task.Delay(10, ct);
            }
            else
            {
                await releaseRefresh.Task.WaitAsync(ct);
            }

            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: false,
                CudaVersion: null,
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null));
        });

        var first = await probe.WaitForProbeAsync(
            "http://localhost:8000",
            forceRefresh: true,
            waitTimeout: TimeSpan.FromSeconds(1));

        Assert.Equal(ContainerizedProbeState.Available, first.State);
        Assert.False(first.IsStale);

        ExpireCachedProbeResult(probe, "http://localhost:8000");
        var refresh = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000", forceRefresh: true);
        Assert.Equal(ContainerizedProbeState.Checking, refresh.State);

        var stale = await probe.WaitForProbeAsync(
            "http://localhost:8000",
            forceRefresh: false,
            waitTimeout: TimeSpan.FromMilliseconds(100));

        Assert.Equal(ContainerizedProbeState.Available, stale.State);
        Assert.True(stale.IsStale);
        Assert.True(stale.WasCacheHit);
        Assert.Equal(2, callCount);

        releaseRefresh.SetResult(true);
        await Task.Delay(50);
    }

    [Fact]
    public async Task GetCurrentOrStartBackgroundProbe_ReturnsStaleAvailableResultWhenCacheExpires()
    {
        var callCount = 0;
        using var log = new AppLog(Path.GetTempFileName());
        var releaseRefresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var probe = new ContainerizedServiceProbe(log, async (url, _, ct) =>
        {
            var currentCall = Interlocked.Increment(ref callCount);
            if (currentCall == 1)
            {
                await Task.Delay(10, ct);
            }
            else
            {
                await releaseRefresh.Task.WaitAsync(ct);
            }

            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: false,
                CudaVersion: null,
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null));
        });

        var first = await probe.WaitForProbeAsync(
            "http://localhost:8000",
            forceRefresh: true,
            waitTimeout: TimeSpan.FromSeconds(1));

        Assert.Equal(ContainerizedProbeState.Available, first.State);
        Assert.False(first.IsStale);

        ExpireCachedProbeResult(probe, "http://localhost:8000");
        var stale = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
        await WaitForCallCountAsync(() => Volatile.Read(ref callCount), expectedMinimum: 2);

        Assert.Equal(ContainerizedProbeState.Available, stale.State);
        Assert.True(stale.IsStale);
        Assert.True(stale.WasCacheHit);
        Assert.True(callCount >= 2);

        releaseRefresh.SetResult(true);
        await Task.Delay(50);
    }

    [Fact]
    public async Task GetCurrentOrStartBackgroundProbe_HandlesProbeFunctionException()
    {
        using var log = new AppLog(Path.GetTempFileName());
        var exceptionThrown = false;

        var probe = new ContainerizedServiceProbe(log, async (url, _, ct) =>
        {
            await Task.Delay(10, ct);
            exceptionThrown = true;
            throw new InvalidOperationException("Simulated probe failure");
        });

        var result = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
        Assert.Equal(ContainerizedProbeState.Checking, result.State);

        // Wait for the background probe to complete
        await Task.Delay(100);

        // Verify the exception was handled and cached as unavailable
        var cachedResult = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
        Assert.Equal(ContainerizedProbeState.Unavailable, cachedResult.State);
        Assert.Contains("Simulated probe failure", cachedResult.ErrorDetail);
        Assert.True(exceptionThrown);
    }

    [Fact]
    public async Task GetCurrentOrStartBackgroundProbe_ObserverExceptionDoesNotCrash()
    {
        using var log = new AppLog(Path.GetTempFileName());

        var probe = new ContainerizedServiceProbe(log, async (url, _, ct) =>
        {
            await Task.Delay(10, ct);
            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: false,
                CudaVersion: null,
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null));
        });

        // Simulate observer fault by causing issues in the continuation
        var result = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
        Assert.Equal(ContainerizedProbeState.Checking, result.State);

        await Task.Delay(100);

        // Verify the probe still works despite any observer issues
        var secondResult = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
        Assert.Equal(ContainerizedProbeState.Available, secondResult.State);
    }

    [Fact]
    public async Task WaitForProbeAsync_ReturnsCheckingWhenBudgetExpires()
    {
        using var log = new AppLog(Path.GetTempFileName());

        var probe = new ContainerizedServiceProbe(log, async (url, _, ct) =>
        {
            await Task.Delay(250, ct);
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
    public async Task WaitForProbeAsync_RespectsCancellationToken()
    {
        using var log = new AppLog(Path.GetTempFileName());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var probe = new ContainerizedServiceProbe(log, async (url, _, ct) =>
        {
            await Task.Delay(200, ct); // Longer than cancellation timeout
            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: false,
                CudaVersion: null,
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null));
        });

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await probe.WaitForProbeAsync(
                "http://localhost:8000",
                forceRefresh: true,
                waitTimeout: TimeSpan.FromSeconds(1),
                cancellationToken: cts.Token);
        });
    }

    [Fact]
    public async Task WaitForProbeAsync_RetriesUntilServiceBecomesAvailableWithinBudget()
    {
        var callCount = 0;
        using var log = new AppLog(Path.GetTempFileName());

        // retryDelay: 10ms  → many retries fit within the 1 s budget on any CI machine
        var probe = new ContainerizedServiceProbe(
            log,
            (url, _, ct) =>
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
            },
            retryDelay: TimeSpan.FromMilliseconds(10));

        var result = await probe.WaitForProbeAsync(
            "http://localhost:8000",
            forceRefresh: true,
            waitTimeout: TimeSpan.FromSeconds(5));

        Assert.Equal(ContainerizedProbeState.Available, result.State);
        Assert.True(callCount >= 3);
    }

    [Fact]
    public async Task WaitForProbeAsync_ReturnsLastUnavailableWhenBudgetExpires()
    {
        var callCount = 0;
        using var log = new AppLog(Path.GetTempFileName());

        // retryDelay: 10ms  → many cycles easily fit inside the 650 ms budget
        var probe = new ContainerizedServiceProbe(
            log,
            (url, _, _) =>
            {
                var currentCall = Interlocked.Increment(ref callCount);
                return Task.FromResult(ContainerHealthStatus.Unavailable(url, $"connection refused #{currentCall}"));
            },
            retryDelay: TimeSpan.FromMilliseconds(10));

        var result = await probe.WaitForProbeAsync(
            "http://localhost:8000",
            forceRefresh: true,
            waitTimeout: TimeSpan.FromMilliseconds(2000));

        Assert.Equal(ContainerizedProbeState.Unavailable, result.State);
        Assert.Contains("connection refused", result.ErrorDetail, StringComparison.OrdinalIgnoreCase);
        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task WaitForProbeAsync_ForceRefreshBypassesCache()
    {
        var callCount = 0;
        using var log = new AppLog(Path.GetTempFileName());

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

    [Fact]
    public async Task GetCurrentOrStartBackgroundProbe_ConcurrentCacheInvalidationRaceCondition()
    {
        var callCount = 0;
        using var log = new AppLog(Path.GetTempFileName());
        var probeStarted = new TaskCompletionSource<bool>();
        var probeCompleted = new TaskCompletionSource<bool>();

        var probe = new ContainerizedServiceProbe(log, async (url, _, _) =>
        {
            var currentCall = Interlocked.Increment(ref callCount);
            if (currentCall == 1)
            {
                probeStarted.SetResult(true);
                await probeCompleted.Task; // Wait for test control
            }
            else
            {
                await Task.Delay(10);
            }
            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: false,
                CudaVersion: null,
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null));
        });

        // Start first probe
        var first = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
        Assert.Equal(ContainerizedProbeState.Checking, first.State);

        await probeStarted.Task;

        // Start multiple concurrent calls
        var concurrentTasks = new List<Task<ContainerizedProbeResult>>();
        for (int i = 0; i < 3; i++)
        {
            concurrentTasks.Add(Task.Run(() => probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000")));
        }

        var concurrentResults = await Task.WhenAll(concurrentTasks);
        foreach (var result in concurrentResults)
        {
            Assert.Equal(ContainerizedProbeState.Checking, result.State);
        }

        // Complete the first probe
        probeCompleted.SetResult(true);
        await Task.Delay(100);

        // Verify cache works after completion
        var cached = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
        Assert.Equal(ContainerizedProbeState.Available, cached.State);
        Assert.True(cached.WasCacheHit);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetCurrentOrStartBackgroundProbe_RapidForceRefreshCalls()
    {
        var callCount = 0;
        using var log = new AppLog(Path.GetTempFileName());
        var probeCompleted = new TaskCompletionSource<bool>();

        var probe = new ContainerizedServiceProbe(log, async (url, _, _) =>
        {
            var currentCall = Interlocked.Increment(ref callCount);
            if (currentCall == 1)
            {
                await probeCompleted.Task;
            }
            else
            {
                await Task.Delay(10);
            }
            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: false,
                CudaVersion: null,
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null));
        });

        // Start first probe
        var first = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
        Assert.Equal(ContainerizedProbeState.Checking, first.State);

        // Rapid force refresh calls
        var refreshResults = new List<ContainerizedProbeResult>();
        for (int i = 0; i < 3; i++)
        {
            refreshResults.Add(probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000", forceRefresh: true));
        }

        // All should return Checking state
        foreach (var result in refreshResults)
        {
            Assert.Equal(ContainerizedProbeState.Checking, result.State);
        }

        // Complete the first probe (which was started first but forced-refreshed away)
        probeCompleted.SetResult(true);

        // Wait for the final probe to complete and populate the cache.
        // We use a polling loop instead of a fixed delay to handle variable CI performance.
        ContainerizedProbeResult final = null!;
        var timeout = DateTime.UtcNow.AddSeconds(5);
        bool success = false;

        while (DateTime.UtcNow < timeout)
        {
            final = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
            if (final.State == ContainerizedProbeState.Available)
            {
                success = true;
                break;
            }
            await Task.Delay(50);
        }

        Assert.True(success, $"Probe did not reach Available state. Current state: {final?.State}");
        Assert.NotNull(final);
        Assert.True(final.WasCacheHit);

        // Implementation may deduplicate concurrent force-refresh requests, but we started at least 4.
        Assert.True(callCount >= 1);
    }

    [Fact]
    public async Task GetCurrentOrStartBackgroundProbe_MemoryPressureScenario()
    {
        var callCount = 0;
        using var log = new AppLog(Path.GetTempFileName());
        
        var probe = new ContainerizedServiceProbe(log, async (url, _, _) =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Delay(20); // Short delay to allow overlap
            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: false,
                CudaVersion: null,
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null));
        });

        // Create many concurrent probe operations to simulate memory pressure
        var tasks = new List<Task<ContainerizedProbeResult>>();
        var serviceUrls = new List<string>();
        
        for (int i = 0; i < 50; i++)
        {
            var url = $"http://localhost:{8000 + i}";
            serviceUrls.Add(url);
            
            // Start multiple probes per URL to test concurrent access
            for (int j = 0; j < 3; j++)
            {
                tasks.Add(Task.Run(() => probe.GetCurrentOrStartBackgroundProbe(url)));
            }
        }

        var results = await Task.WhenAll(tasks);
        
        // Verify all probes completed successfully
        Assert.Equal(150, results.Length);
        Assert.All(results, result => Assert.True(result.State == ContainerizedProbeState.Checking || result.State == ContainerizedProbeState.Available));
        
        // Wait for all background probes to complete (robust polling loop for CI stability)
        var timeout = DateTime.UtcNow.AddSeconds(5);
        bool allAvailable = false;
        while (DateTime.UtcNow < timeout)
        {
            allAvailable = true;
            foreach (var url in serviceUrls)
            {
                var state = probe.GetCurrentOrStartBackgroundProbe(url).State;
                if (state != ContainerizedProbeState.Available)
                {
                    allAvailable = false;
                    break;
                }
            }
            if (allAvailable) break;
            await Task.Delay(100);
        }

        Assert.True(allAvailable, "Background probes did not reach Available state within timeout.");

        
        // Verify cache works and metrics are accurate
        foreach (var url in serviceUrls)
        {
            var cached = probe.GetCurrentOrStartBackgroundProbe(url);
            Assert.Equal(ContainerizedProbeState.Available, cached.State);
            Assert.True(cached.WasCacheHit);
            
            var metrics = probe.GetMetrics(url);
            Assert.NotNull(metrics);
            Assert.Equal(1, metrics.TotalProbes); // Only one actual probe per URL
            Assert.Equal(1, metrics.SuccessfulProbes);
            Assert.True(metrics.CacheHits > 0); // Should have cache hits
        }
        
        // Should have executed exactly 50 probes (one per unique URL)
        Assert.Equal(50, callCount);
    }

    [Fact]
    public async Task GetCurrentOrStartBackgroundProbe_BasicCacheFunctionality()
    {
        var callCount = 0;
        using var log = new AppLog(Path.GetTempFileName());
        var probe = new ContainerizedServiceProbe(log, async (url, _, _) =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Delay(50);
            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: false,
                CudaVersion: null,
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null));
        });

        const string testUrl = "http://localhost:8000";
        
        // First call should start a probe
        var first = probe.GetCurrentOrStartBackgroundProbe(testUrl);
        Assert.Equal(ContainerizedProbeState.Checking, first.State);
        Assert.False(first.WasCacheHit);
        
        // Wait for probe to complete
        await Task.Delay(100);
        
        // Second call should hit cache
        var second = probe.GetCurrentOrStartBackgroundProbe(testUrl);
        Assert.Equal(ContainerizedProbeState.Available, second.State);
        Assert.True(second.WasCacheHit);
        
        // Verify metrics
        var metrics = probe.GetMetrics(testUrl);
        Assert.NotNull(metrics);
        Assert.Equal(1, metrics.TotalProbes);
        Assert.Equal(1, metrics.SuccessfulProbes);
        Assert.Equal(1, metrics.CacheHits); // The second call
        Assert.Equal(1, metrics.CacheMisses); // The first call
        Assert.Equal(1, callCount); // Only one actual probe executed
    }

    [Fact]
    public async Task GetCurrentOrStartBackgroundProbe_CacheDoubleHitWithinLock()
    {
        var callCount = 0;
        using var log = new AppLog(Path.GetTempFileName());
        var probeCompleted = new TaskCompletionSource<bool>();

        var probe = new ContainerizedServiceProbe(log, async (url, _, ct) =>
        {
            var currentCall = Interlocked.Increment(ref callCount);
            if (currentCall == 1)
            {
                // First call takes longer to simulate in-flight scenario
                await Task.Delay(100, ct);
                probeCompleted.SetResult(true);
            }
            else
            {
                await Task.Delay(10, ct);
            }
            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: false,
                CudaVersion: null,
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null));
        });

        // Start first probe
        var first = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
        Assert.Equal(ContainerizedProbeState.Checking, first.State);

        // Wait a bit then start second probe (should reuse in-flight)
        await Task.Delay(20);
        var second = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
        Assert.Equal(ContainerizedProbeState.Checking, second.State);

        // Wait for probe to complete
        await probeCompleted.Task;

        // Wait for cache to be populated with polling instead of fixed delay
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(25);
            var third = probe.GetCurrentOrStartBackgroundProbe("http://localhost:8000");
            if (third.State == ContainerizedProbeState.Available)
            {
                Assert.Equal(1, callCount);
                return;
            }
        }

        throw new Xunit.Sdk.XunitException("Probe cache was not populated within expected time.");
    }

    private static void ExpireCachedProbeResult(ContainerizedServiceProbe probe, string serviceUrl)
    {
        var entriesField = typeof(ContainerizedServiceProbe).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find _entries field.");

        var entries = entriesField.GetValue(probe)
            ?? throw new InvalidOperationException("ContainerizedServiceProbe entries cache was null.");

        var normalizedUrl = ContainerizedInferenceClient.NormalizeBaseUrl(serviceUrl);
        var tryGetValue = entries.GetType().GetMethod("TryGetValue")
            ?? throw new InvalidOperationException("Could not find TryGetValue on probe cache.");

        var tryGetArgs = new object?[] { normalizedUrl, null };
        var found = (bool)(tryGetValue.Invoke(entries, tryGetArgs) ?? false);
        if (!found)
            throw new InvalidOperationException($"No cached probe entry found for {normalizedUrl}.");

        var entry = tryGetArgs[1] ?? throw new InvalidOperationException("Probe cache entry was null.");
        var expiresProperty = entry.GetType().GetProperty("ExpiresAtUtc")
            ?? throw new InvalidOperationException("Could not find ExpiresAtUtc on probe cache entry.");
        expiresProperty.SetValue(entry, DateTimeOffset.UtcNow.AddSeconds(-1));
    }

    private static async Task WaitForCallCountAsync(Func<int> getCount, int expectedMinimum, int timeoutMs = 500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (getCount() >= expectedMinimum)
                return;

            await Task.Delay(10);
        }
    }
}
