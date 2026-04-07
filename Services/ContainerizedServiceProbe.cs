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
    ContainerCapabilitiesSnapshot? Capabilities = null,
    TimeSpan? Duration = null,
    bool WasCacheHit = false);

public sealed class ContainerizedServiceProbe : IProbeMetricsReporter
{
    private static readonly TimeSpan PassiveProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AvailableTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnavailableTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultWaitRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<string, ProbeEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly AppLog _log;
    private readonly Func<string, TimeSpan, CancellationToken, Task<ContainerHealthStatus>> _probeFunc;
    private readonly ContainerizedProbeMetrics _metrics;

    // Configurable for testing: controls the pause between successive probe retries
    // inside WaitForProbeAsync. Defaults to 250 ms in production.
    private readonly TimeSpan _waitRetryDelay;

    public ContainerizedServiceProbe(
        AppLog log,
        Func<string, TimeSpan, CancellationToken, Task<ContainerHealthStatus>>? probeFunc = null,
        TimeSpan? retryDelay = null,
        ContainerizedProbeMetrics? metrics = null)
    {
        if (retryDelay.HasValue && retryDelay.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay), retryDelay, "Retry delay must be greater than TimeSpan.Zero.");
        }
        _log = log;
        _probeFunc = probeFunc ?? ContainerizedInferenceClient.CheckHealthAsync;
        _waitRetryDelay = retryDelay ?? DefaultWaitRetryDelay;
        _metrics = metrics ?? new ContainerizedProbeMetrics();
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
 
        lock (entry.Gate)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            
            // Check cache first to prevent race condition with in-flight task completion
            if (!forceRefresh && entry.CachedResult is not null && entry.ExpiresAtUtc > nowUtc)
            {
                _log.Info($"Container probe cache hit: url={normalizedUrl}, state={entry.CachedResult.State}");
                ReportCacheAccess(normalizedUrl, true);
                // Return cached result with WasCacheHit flag set
                return entry.CachedResult with { WasCacheHit = true };
            }

            if (entry.InFlightTask is not null && !forceRefresh)
            {
                // Double-check cache: the task may have completed between our first check and now
                if (!forceRefresh && entry.CachedResult is not null && entry.ExpiresAtUtc > nowUtc)
                {
                    _log.Info($"Container probe cache hit (after race): url={normalizedUrl}, state={entry.CachedResult.State}");
                    return entry.CachedResult;
                }
                _log.Info($"Container probe reuse in-flight: url={normalizedUrl}");
                return Checking(normalizedUrl);
            }

            // If forceRefresh and there's an in-flight task, cancel it and start a fresh probe.
            if (forceRefresh && entry.InFlightTask is not null)
            {
                _log.Info($"Container probe force refresh: url={normalizedUrl}, cancelling previous probe and starting new one");
                try
                {
                    entry.Cts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // CTS already disposed — ignore.
                }
                entry.CachedResult = null;
            }


            entry.Cts = new CancellationTokenSource();
            entry.InFlightTask = StartProbeTask(normalizedUrl, PassiveProbeTimeout, entry.Cts.Token);
            _log.Info($"Container probe start: url={normalizedUrl}, timeoutMs={PassiveProbeTimeout.TotalMilliseconds}, mode=background, forceRefresh={forceRefresh}");
            
            // Fire-and-forget with proper exception handling to prevent resource leaks
            ObserveCompletionWithFaultHandling(normalizedUrl, entry, entry.InFlightTask);
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

    private async void ObserveCompletionWithFaultHandling(
        string normalizedUrl,
        ProbeEntry entry,
        Task<ContainerizedProbeResult> task)
    {
        try
        {
            await ObserveCompletionAsync(normalizedUrl, entry, task).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // This prevents unobserved task exceptions and ensures logging
            _log.Error($"Container probe observer fault: url={normalizedUrl}, error={ex.Message}", ex);
        }
    }

    private async Task ObserveCompletionAsync(
        string normalizedUrl,
        ProbeEntry entry,
        Task<ContainerizedProbeResult> task)
    {
        ContainerizedProbeResult result;
        try
        {
            result = await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Info($"Container probe cancelled: url={normalizedUrl}");
            ReportProbeResult(normalizedUrl, ProbeResult.Cancellation, duration: null, wasCacheHit: false, errorDetail: null);
            lock (entry.Gate)
            {
                if (ReferenceEquals(entry.InFlightTask, task))
                {
                    entry.InFlightTask = null;
                    entry.Cts?.Dispose();
                    entry.Cts = null;
                }
            }
            return;
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

        try
        {
            lock (entry.Gate)
            {
                if (ReferenceEquals(entry.InFlightTask, task))
                {
                    entry.InFlightTask = null;
                    entry.Cts?.Dispose();
                    entry.Cts = null;

                    if (ttl > TimeSpan.Zero)
                    {
                        entry.CachedResult = result;
                        entry.ExpiresAtUtc = DateTimeOffset.UtcNow.Add(ttl);
                    }
                }
            }

            // Report metrics for the completed probe (outside lock)
            var probeResult = result.State switch
            {
                ContainerizedProbeState.Available => ProbeResult.Success,
                ContainerizedProbeState.Unavailable => ProbeResult.Failure,
                _ => ProbeResult.Failure
            };
            
            ReportProbeResult(normalizedUrl, probeResult, result.Duration, result.WasCacheHit, result.ErrorDetail);

            _log.Info(
                $"Container probe complete: url={normalizedUrl}, state={result.State}, " +
                $"detail={result.ErrorDetail ?? "<none>"}");
        }
        catch (Exception ex)
        {
            // Catch any exceptions in the logging section to prevent process crash
            _log.Error($"Container probe observer failed during completion: url={normalizedUrl}, error={ex.Message}", ex);
        }
    }

    private Task<ContainerizedProbeResult> StartProbeTask(string normalizedUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var health = await _probeFunc(normalizedUrl, timeout, cancellationToken);
                stopwatch.Stop();

                return new ContainerizedProbeResult(
                    normalizedUrl,
                    health.IsAvailable ? ContainerizedProbeState.Available : ContainerizedProbeState.Unavailable,
                    DateTimeOffset.UtcNow,
                    health.ErrorMessage,
                    health.CudaAvailable,
                    health.CudaVersion,
                    health.Capabilities,
                    stopwatch.Elapsed,
                    WasCacheHit: false);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new ContainerizedProbeResult(
                    normalizedUrl,
                    ContainerizedProbeState.Unavailable,
                    DateTimeOffset.UtcNow,
                    ex.Message,
                    false,
                    null,
                    null,
                    stopwatch.Elapsed,
                    WasCacheHit: false);
            }
        }, cancellationToken);
    }

    private static ContainerizedProbeResult Checking(string normalizedUrl) =>
        new(
            normalizedUrl,
            ContainerizedProbeState.Checking,
            DateTimeOffset.UtcNow,
            WasCacheHit: false);

    // IProbeMetricsReporter implementation
    public void ReportProbeResult(string serviceUrl, ProbeResult result, TimeSpan? duration = null, bool wasCacheHit = false, string? errorDetail = null)
    {
        var metrics = _metrics.GetOrCreateMetrics(serviceUrl);
        
        switch (result)
        {
            case ProbeResult.Success:
                metrics.RecordSuccess(duration ?? TimeSpan.Zero, wasCacheHit);
                break;
            case ProbeResult.Failure:
                metrics.RecordFailure(errorDetail, wasCacheHit);
                break;
            case ProbeResult.Cancellation:
                metrics.RecordCancellation();
                break;
        }
    }

    public void ReportCacheAccess(string serviceUrl, bool wasHit)
    {
        var metrics = _metrics.GetOrCreateMetrics(serviceUrl);
        metrics.RecordCacheAccess(wasHit);
    }

    public ServiceMetrics[] GetAllMetrics()
    {
        return _metrics.GetAllMetrics();
    }

    public ServiceMetrics? GetMetrics(string serviceUrl)
    {
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return null;
            
        var normalizedUrl = ContainerizedInferenceClient.NormalizeBaseUrl(serviceUrl);
        var allMetrics = _metrics.GetAllMetrics();
        foreach (var metric in allMetrics)
        {
            if (string.Equals(metric.ServiceUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase))
                return metric;
        }
        return null;
    }

    private sealed class ProbeEntry
    {
        public object Gate { get; } = new();
        public Task<ContainerizedProbeResult>? InFlightTask { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public ContainerizedProbeResult? CachedResult { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }
}
