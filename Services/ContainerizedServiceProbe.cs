using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    string? CapabilitiesError = null,
    TimeSpan? Duration = null,
    bool WasCacheHit = false,
    bool IsStale = false,
    IReadOnlyDictionary<string, ContainerProviderHealthSnapshot>? ProviderHealth = null,
    int ActiveRequests = 0,
    int ActiveQwenRequests = 0,
    int ActiveDiarizationRequests = 0,
    bool Busy = false,
    string? BusyReason = null,
    int QwenMaxConcurrency = 0,
    int QwenQueueDepth = 0,
    double? QwenLastQueueWaitMs = null,
    double? QwenLastGenerationMs = null,
    double? QwenLastReferencePrepMs = null,
    double? QwenLastWarmupMs = null);

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

    /// <summary>
    /// Gets the current probe result for the specified containerized service URL or starts a background probe and returns a checking result.
    /// </summary>
    /// <param name="serviceUrl">The service base URL to probe. If null or blank, an unavailable result is returned with an explanatory error detail.</param>
    /// <param name="forceRefresh">If true, bypasses cached results and forces a new background probe (cancelling any in-flight probe for the same URL).</param>
    /// <returns>
    /// A <see cref="ContainerizedProbeResult"/> that is one of:
    /// - a cached available or unavailable result (cached returns have <c>WasCacheHit = true</c>),
    /// - a stale available result (returned during an in-flight refresh and marked with <c>IsStale = true</c>),
    /// - or a Checking result when a background probe has been started or an in-flight probe is being reused.
    /// </returns>
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
                    ReportCacheAccess(normalizedUrl, true);
                    return entry.CachedResult with { WasCacheHit = true };
                }

                // If the in-flight task completed but the observer hasn't cached the result yet,
                // extract the result directly to prevent a spurious Checking state.
                if (entry.InFlightTask.IsCompletedSuccessfully)
                {
                    var completedResult = entry.InFlightTask.Result;
                    var ttl = completedResult.State switch
                    {
                        ContainerizedProbeState.Available => AvailableTtl,
                        ContainerizedProbeState.Unavailable => UnavailableTtl,
                        _ => TimeSpan.Zero,
                    };
                    // Clear the in-flight task so the observer skips its cache update
                    // (ObserveCompletionAsync checks ReferenceEquals before writing).
                    entry.InFlightTask = null;
                    entry.Cts?.Dispose();
                    entry.Cts = null;
                    if (ttl > TimeSpan.Zero)
                    {
                        entry.CachedResult = completedResult;
                        entry.ExpiresAtUtc = DateTimeOffset.UtcNow.Add(ttl);
                        if (completedResult.State == ContainerizedProbeState.Available)
                            entry.LastAvailableResult = completedResult;
                    }
                    _log.Info($"Container probe completed (observer not yet run): url={normalizedUrl}, state={completedResult.State}");
                    return completedResult;
                }

                var staleAvailable = GetStaleAvailableResult(entry);
                if (staleAvailable is not null)
                {
                    _log.Info($"Container probe returning stale available result while refresh is in-flight: url={normalizedUrl}");
                    ReportCacheAccess(normalizedUrl, true);
                    return staleAvailable with { WasCacheHit = true, IsStale = true };
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
            
            // Fire-and-forget; FireAndForgetAsync routes unhandled exceptions to the log.
            ObserveCompletionAsync(normalizedUrl, entry, entry.InFlightTask).FireAndForgetAsync(_log, "Containerized probe observer");
            var staleCachedAvailable = !forceRefresh ? GetStaleAvailableResult(entry) : null;
            if (staleCachedAvailable is not null)
            {
                _log.Info($"Container probe returning stale available result while refresh is starting: url={normalizedUrl}");
                ReportCacheAccess(normalizedUrl, true);
                return staleCachedAvailable with { WasCacheHit = true, IsStale = true };
            }

            return Checking(normalizedUrl);
        }
    }

    /// <summary>
    /// Waits until the service probe reports an Available state or the wait budget is exhausted.
    /// </summary>
    /// <param name="serviceUrl">The service base URL to probe; blank or null is treated as an empty URL.</param>
    /// <param name="forceRefresh">If true, bypasses cached results and starts a fresh probe attempt (used to bypass negative cache).</param>
    /// <param name="waitTimeout">Maximum time to wait for an Available result; if null, the passive probe timeout is used.</param>
    /// <param name="cancellationToken">Token to cancel the wait operation.</param>
    /// <returns>
    /// The observed probe result: an Available result if one was observed within the budget; otherwise the most recent Unavailable result if any, or the most recent stale Available result if present, or a Checking result when no prior outcomes exist.
    /// </returns>
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
        ContainerizedProbeResult? lastAvailable = null;
 
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
 
            var result = GetCurrentOrStartBackgroundProbe(serviceUrl, forceRefresh);
 
            if (result.State == ContainerizedProbeState.Available)
            {
                if (!result.IsStale)
                    return result;
                // Stale: keep as fallback but keep waiting for a fresh probe.
                lastAvailable = result;
            }

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

        if (lastAvailable != null)
        {
            _log.Info($"Container probe wait budget exhausted: url={normalizedUrl}, returning last stale available state.");
            return lastAvailable;
        }
 
        _log.Info($"Container probe wait budget exhausted: url={normalizedUrl}, returning Checking state.");
        return Checking(normalizedUrl);
    }

    /// <summary>
    /// Observes the completion of an in-flight probe task, updates the entry's cached state and last-available snapshot, clears in-flight tracking on completion or cancellation, and reports probe metrics and logs.
    /// </summary>
    /// <param name="normalizedUrl">Normalized base URL of the service being probed.</param>
    /// <param name="entry">The per-service probe entry containing coordination and cached results to update.</param>
    /// <param name="task">The probe task whose completion is being observed.</param>
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
                        if (result.State == ContainerizedProbeState.Available)
                            entry.LastAvailableResult = result;
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

    /// <summary>
    /// Runs the configured health check for the given normalized service URL and produces a ContainerizedProbeResult.
    /// </summary>
    /// <param name="normalizedUrl">The normalized base URL of the containerized service to probe.</param>
    /// <param name="timeout">Maximum time budget for the underlying health check.</param>
    /// <param name="cancellationToken">Token used to cancel the probe.</param>
    /// <returns>
    /// A ContainerizedProbeResult representing the probe outcome (state, timing, CUDA and capabilities info, and error details).
    /// If the underlying probe throws an exception other than cancellation, the method returns an Unavailable result with ErrorDetail set to the exception message.
    /// </returns>
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
                    health.CapabilitiesError,
                    stopwatch.Elapsed,
                    WasCacheHit: false,
                    ProviderHealth: health.ProviderHealth,
                    ActiveRequests: health.ActiveRequests,
                    ActiveQwenRequests: health.ActiveQwenRequests,
                    ActiveDiarizationRequests: health.ActiveDiarizationRequests,
                    Busy: health.Busy,
                    BusyReason: health.BusyReason,
                    QwenMaxConcurrency: health.QwenMaxConcurrency,
                    QwenQueueDepth: health.QwenQueueDepth,
                    QwenLastQueueWaitMs: health.QwenLastQueueWaitMs,
                    QwenLastGenerationMs: health.QwenLastGenerationMs,
                    QwenLastReferencePrepMs: health.QwenLastReferencePrepMs,
                    QwenLastWarmupMs: health.QwenLastWarmupMs);
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
        public ContainerizedProbeResult? LastAvailableResult { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    private static ContainerizedProbeResult? GetStaleAvailableResult(ProbeEntry entry) =>
        entry.LastAvailableResult is { State: ContainerizedProbeState.Available } lastAvailable
            ? lastAvailable
            : entry.CachedResult is { State: ContainerizedProbeState.Available } cachedAvailable
                ? cachedAvailable
                : null;
}
