using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public enum ContainerizedProbeState
{
    Checking,
    Available,
    Unavailable,
}

public sealed record ContainerizedProbeResult(
    string ServiceUrl,
    ContainerizedProbeState State,
    DateTimeOffset CheckedAtUtc,
    string? ErrorDetail = null,
    bool CudaAvailable = false,
    string? CudaVersion = null,
    ContainerCapabilitiesSnapshot? Capabilities = null);

public sealed class ContainerizedServiceProbe
{
    private static readonly TimeSpan PassiveProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AvailableTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnavailableTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultWaitRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<string, ProbeEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly AppLog _log;
    private readonly Func<string, TimeSpan, CancellationToken, Task<ContainerHealthStatus>> _probeFunc;

    // Configurable for testing: controls the pause between successive probe retries
    // inside WaitForProbeAsync. Defaults to 250 ms in production.
    private readonly TimeSpan _waitRetryDelay;

    public ContainerizedServiceProbe(
        AppLog log,
        Func<string, TimeSpan, CancellationToken, Task<ContainerHealthStatus>>? probeFunc = null,
        TimeSpan? retryDelay = null)
    {
        if (retryDelay.HasValue && retryDelay.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay), retryDelay, "Retry delay must be greater than TimeSpan.Zero.");
        }
        _log = log;
        _probeFunc = probeFunc ?? ContainerizedInferenceClient.CheckHealthAsync;
        _waitRetryDelay = retryDelay ?? DefaultWaitRetryDelay;
    }

    public ContainerizedProbeResult GetCurrentOrStartBackgroundProbe(string? serviceUrl)
    {
        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            return new ContainerizedProbeResult(
                string.Empty,
                ContainerizedProbeState.Unavailable,
                DateTimeOffset.UtcNow,
                "No containerized inference service URL configured.");
        }

        var normalizedUrl = ContainerizedInferenceClient.NormalizeBaseUrl(serviceUrl);
        var entry = _entries.GetOrAdd(normalizedUrl, _ => new ProbeEntry());
        var nowUtc = DateTimeOffset.UtcNow;

        lock (entry.Gate)
        {
            if (entry.CachedResult is not null && entry.ExpiresAtUtc > nowUtc)
            {
                _log.Info($"Container probe cache hit: url={normalizedUrl}, state={entry.CachedResult.State}");
                return entry.CachedResult;
            }

            if (entry.InFlightTask is not null)
            {
                _log.Info($"Container probe reuse in-flight: url={normalizedUrl}");
                return Checking(normalizedUrl);
            }

            entry.InFlightTask = StartProbeTask(normalizedUrl, PassiveProbeTimeout);
            _log.Info($"Container probe start: url={normalizedUrl}, timeoutMs={PassiveProbeTimeout.TotalMilliseconds}, mode=background");
            ObserveCompletion(normalizedUrl, entry, entry.InFlightTask);
            return Checking(normalizedUrl);
        }
    }

    public async Task<ContainerizedProbeResult> WaitForProbeAsync(
        string? serviceUrl,
        bool forceRefresh = false,
        TimeSpan? waitTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            return new ContainerizedProbeResult(
                string.Empty,
                ContainerizedProbeState.Unavailable,
                DateTimeOffset.UtcNow,
                "No containerized inference service URL configured.");
        }

        var normalizedUrl = ContainerizedInferenceClient.NormalizeBaseUrl(serviceUrl);
        var entry = _entries.GetOrAdd(normalizedUrl, _ => new ProbeEntry());
        var budget = waitTimeout ?? PassiveProbeTimeout;
        var startedAt = Stopwatch.StartNew();
        ContainerizedProbeResult? lastCompletedResult = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nowUtc = DateTimeOffset.UtcNow;
            if (!forceRefresh)
            {
                lock (entry.Gate)
                {
                    if (entry.CachedResult is not null && entry.ExpiresAtUtc > nowUtc)
                    {
                        if (entry.CachedResult.State == ContainerizedProbeState.Available)
                        {
                            _log.Info($"Container probe cache hit: url={normalizedUrl}, state={entry.CachedResult.State}, mode=wait");
                            return entry.CachedResult;
                        }

                        _log.Info(
                            $"Container probe cache stale-for-wait: url={normalizedUrl}, state={entry.CachedResult.State}, mode=wait; continuing retries");
                    }
                }
            }

            var remaining = budget - startedAt.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            Task<ContainerizedProbeResult> task;
            lock (entry.Gate)
            {
                if (entry.InFlightTask is not null)
                {
                    _log.Info($"Container probe reuse in-flight: url={normalizedUrl}, mode=wait");
                    task = entry.InFlightTask;
                }
                else
                {
                    task = StartProbeTask(normalizedUrl, remaining);
                    entry.InFlightTask = task;
                    _log.Info($"Container probe start: url={normalizedUrl}, timeoutMs={remaining.TotalMilliseconds}, mode=wait, forceRefresh={forceRefresh}");
                    ObserveCompletion(normalizedUrl, entry, task);
                }
            }

            // After dispatching or reusing the first probe, stop bypassing the cache
            // so that subsequent loop iterations can short-circuit on a populated entry.
            forceRefresh = false;

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(remaining);

            try
            {
                var result = await task.WaitAsync(attemptCts.Token);
                lastCompletedResult = result;
                if (result.State == ContainerizedProbeState.Available)
                    return result;

                var retryDelay = budget - startedAt.Elapsed;
                if (retryDelay <= TimeSpan.Zero)
                    break;

                var actualDelay = retryDelay < _waitRetryDelay ? retryDelay : _waitRetryDelay;
                _log.Info(
                    $"Container probe wait retrying: url={normalizedUrl}, state={result.State}, " +
                    $"detail={result.ErrorDetail ?? "<none>"}, retryInMs={actualDelay.TotalMilliseconds}");
                await Task.Delay(actualDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        if (lastCompletedResult is not null)
        {
            _log.Info(
                $"Container probe wait budget exhausted: url={normalizedUrl}, " +
                $"returning_state={lastCompletedResult.State}, detail={lastCompletedResult.ErrorDetail ?? "<none>"}");
            return lastCompletedResult;
        }

        _log.Info($"Container probe wait timed out before any probe completed: url={normalizedUrl}, timeoutMs={budget.TotalMilliseconds}");
        return Checking(normalizedUrl);
    }

    private async void ObserveCompletion(
        string normalizedUrl,
        ProbeEntry entry,
        Task<ContainerizedProbeResult> task)
    {
        ContainerizedProbeResult result;
        try
        {
            result = await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Info($"Container probe observer caught fault: url={normalizedUrl}, error={ex.Message}");
            result = new ContainerizedProbeResult(
                normalizedUrl,
                ContainerizedProbeState.Unavailable,
                DateTimeOffset.UtcNow,
                ex.Message);
        }

        var ttl = result.State switch
        {
            ContainerizedProbeState.Available => AvailableTtl,
            ContainerizedProbeState.Unavailable => UnavailableTtl,
            _ => TimeSpan.Zero,
        };

        lock (entry.Gate)
        {
            if (ReferenceEquals(entry.InFlightTask, task))
                entry.InFlightTask = null;

            if (ttl > TimeSpan.Zero)
            {
                entry.CachedResult = result;
                entry.ExpiresAtUtc = DateTimeOffset.UtcNow.Add(ttl);
            }
        }

        _log.Info(
            $"Container probe complete: url={normalizedUrl}, state={result.State}, " +
            $"detail={result.ErrorDetail ?? "<none>"}");
    }

    private Task<ContainerizedProbeResult> StartProbeTask(string normalizedUrl, TimeSpan timeout)
    {
        return Task.Run(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var health = await _probeFunc(normalizedUrl, timeout, CancellationToken.None);
                stopwatch.Stop();

                return new ContainerizedProbeResult(
                    normalizedUrl,
                    health.IsAvailable ? ContainerizedProbeState.Available : ContainerizedProbeState.Unavailable,
                    DateTimeOffset.UtcNow,
                    health.ErrorMessage,
                    health.CudaAvailable,
                    health.CudaVersion,
                    health.Capabilities);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new ContainerizedProbeResult(
                    normalizedUrl,
                    ContainerizedProbeState.Unavailable,
                    DateTimeOffset.UtcNow,
                    ex.Message);
            }
        });
    }

    private static ContainerizedProbeResult Checking(string normalizedUrl) =>
        new(
            normalizedUrl,
            ContainerizedProbeState.Checking,
            DateTimeOffset.UtcNow);

    private sealed class ProbeEntry
    {
        public object Gate { get; } = new();
        public Task<ContainerizedProbeResult>? InFlightTask { get; set; }
        public ContainerizedProbeResult? CachedResult { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }
}
