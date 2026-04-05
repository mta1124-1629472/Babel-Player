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

    public ContainerizedProbeResult GetCurrentOrStartBackgroundProbe(
        string? serviceUrl,
        bool forceRefresh = false)
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
            if (!forceRefresh && entry.CachedResult is not null && entry.ExpiresAtUtc > nowUtc)
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
            _log.Info($"Container probe start: url={normalizedUrl}, timeoutMs={PassiveProbeTimeout.TotalMilliseconds}, mode=background, forceRefresh={forceRefresh}");
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
        var budget = waitTimeout ?? PassiveProbeTimeout;
        var deadline = DateTimeOffset.UtcNow + budget;
        var normalizedUrl = string.IsNullOrWhiteSpace(serviceUrl)
            ? string.Empty
            : ContainerizedInferenceClient.NormalizeBaseUrl(serviceUrl);
 
        ContainerizedProbeResult? lastFailure = null;
 
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
 
            var result = GetCurrentOrStartBackgroundProbe(serviceUrl, forceRefresh);
 
            if (result.State == ContainerizedProbeState.Available)
                return result;
 
            if (result.State == ContainerizedProbeState.Unavailable)
            {
                lastFailure = result;
                // If we got a failure, force a refresh on the next loop iteration
                // to bypass the negative cache and start a new probe attempt.
                forceRefresh = true;
            }
            else
            {
                // If it's Checking, we want to keep waiting for the in-flight task
                // without forcing a new one.
                forceRefresh = false;
            }
 
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;
 
            var actualDelay = remaining < _waitRetryDelay ? remaining : _waitRetryDelay;
            await Task.Delay(actualDelay, cancellationToken);
        }
 
        // If we timed out, return the last failure if one exists, otherwise Checking.
        if (lastFailure != null)
        {
            _log.Info($"Container probe wait budget exhausted: url={normalizedUrl}, returning last unavailable state.");
            return lastFailure;
        }
 
        _log.Info($"Container probe wait budget exhausted: url={normalizedUrl}, returning Checking state.");
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
