using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Babel.Player.Services;

/// <summary>
/// Metrics for monitoring containerized service probe effectiveness and health.
/// Thread-safe for concurrent access across probe operations.
/// </summary>
public sealed class ContainerizedProbeMetrics
{
    private readonly ConcurrentDictionary<string, ServiceMetrics> _serviceMetrics = new();
    
    /// <summary>
    /// Gets metrics for a specific service URL, creating a new entry if needed.
    /// </summary>
    public ServiceMetrics GetOrCreateMetrics(string serviceUrl)
    {
        if (string.IsNullOrWhiteSpace(serviceUrl))
            throw new ArgumentException("Service URL cannot be null or empty.", nameof(serviceUrl));
            
        var normalizedUrl = ContainerizedInferenceClient.NormalizeBaseUrl(serviceUrl);
        
        // Basic URL validation after normalization
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) || 
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ArgumentException($"Service URL must be a valid HTTP/HTTPS URL: {normalizedUrl}", nameof(serviceUrl));
        }
        
        return _serviceMetrics.GetOrAdd(normalizedUrl, _ => new ServiceMetrics(normalizedUrl));
    }
    
    /// <summary>
    /// Gets a snapshot of all current metrics.
    /// </summary>
    public ServiceMetrics[] GetAllMetrics()
    {
        return _serviceMetrics.Values.ToArray();
    }
    
    /// <summary>
    /// Resets all metrics to zero.
    /// </summary>
    public void ResetAll()
    {
        // Create a snapshot to avoid race conditions during iteration
        var metricsSnapshot = _serviceMetrics.Values.ToArray();
        foreach (var metrics in metricsSnapshot)
        {
            metrics.Reset();
        }
    }
    
    /// <summary>
    /// Removes metrics for a specific service URL.
    /// </summary>
    public void RemoveMetrics(string serviceUrl)
    {
        var normalizedUrl = ContainerizedInferenceClient.NormalizeBaseUrl(serviceUrl);
        _serviceMetrics.TryRemove(normalizedUrl, out _);
    }
}

/// <summary>
/// Per-service probe metrics tracking success rates, timing, and error patterns.
/// Thread-safe for concurrent access.
/// </summary>
public sealed class ServiceMetrics
{
    private readonly object _gate = new();
    
    public string ServiceUrl { get; }
    
    public long TotalProbes { get; private set; }
    public long SuccessfulProbes { get; private set; }
    public long FailedProbes { get; private set; }
    public long CacheHits { get; private set; }
    public long CacheMisses { get; private set; }
    public long Cancellations { get; private set; }
    
    /// <summary>
    /// Cumulative probe duration in milliseconds.
    /// Use AverageDurationMs for the calculated average.
    /// </summary>
    public long TotalDurationMs { get; private set; }
    
    /// <summary>
    /// Timestamp of the last probe attempt.
    /// </summary>
    public DateTimeOffset LastProbeAtUtc { get; private set; }
    
    /// <summary>
    /// Timestamp of the last successful probe.
    /// </summary>
    public DateTimeOffset LastSuccessAtUtc { get; private set; }
    
    /// <summary>
    /// Error details from the most recent failed probe.
    /// </summary>
    public string? LastError { get; private set; }
    
    /// <summary>
    /// Count of consecutive failures since the last success.
    /// </summary>
    public long ConsecutiveFailures { get; private set; }
    
    public ServiceMetrics(string serviceUrl)
    {
        ServiceUrl = serviceUrl;
        LastProbeAtUtc = DateTimeOffset.MinValue;
        LastSuccessAtUtc = DateTimeOffset.MinValue;
    }
    
    /// <summary>
    /// Success rate as a percentage (0-100). Returns 0 if no probes attempted.
    /// </summary>
    public double SuccessRate => TotalProbes > 0 ? (double)SuccessfulProbes / TotalProbes * 100 : 0;
    
    /// <summary>
    /// Cache hit rate as a percentage (0-100). Returns 0 if no cache accesses.
    /// </summary>
    public double CacheHitRate 
    { 
        get
        {
            var totalAccesses = CacheHits + CacheMisses;
            return totalAccesses > 0 ? (double)CacheHits / totalAccesses * 100 : 0;
        }
    }
    
    /// <summary>
    /// Average probe duration in milliseconds. Returns 0 if no probes completed.
    /// </summary>
    public double AverageDurationMs => SuccessfulProbes > 0 ? (double)TotalDurationMs / SuccessfulProbes : 0;
    
    /// <summary>
    /// Records a successful probe completion.
    /// </summary>
    public void RecordSuccess(TimeSpan duration, bool wasCacheHit)
    {
        lock (_gate)
        {
            TotalProbes++;
            SuccessfulProbes++;
            TotalDurationMs += (long)duration.TotalMilliseconds;
            LastProbeAtUtc = DateTimeOffset.UtcNow;
            LastSuccessAtUtc = DateTimeOffset.UtcNow;
            LastError = null;
            ConsecutiveFailures = 0;
            
            if (wasCacheHit)
                CacheHits++;
            else
                CacheMisses++;
        }
    }
    
    /// <summary>
    /// Records a failed probe completion.
    /// </summary>
    public void RecordFailure(string? errorDetail, bool wasCacheHit)
    {
        lock (_gate)
        {
            TotalProbes++;
            FailedProbes++;
            LastProbeAtUtc = DateTimeOffset.UtcNow;
            LastError = errorDetail;
            ConsecutiveFailures++;
            
            if (wasCacheHit)
                CacheHits++;
            else
                CacheMisses++;
        }
    }
    
    /// <summary>
    /// Records a cancelled probe operation.
    /// </summary>
    public void RecordCancellation()
    {
        lock (_gate)
        {
            TotalProbes++;
            Cancellations++;
            LastProbeAtUtc = DateTimeOffset.UtcNow;
        }
    }
    
    /// <summary>
    /// Records a cache access (hit or miss) without a probe execution.
    /// </summary>
    public void RecordCacheAccess(bool wasHit)
    {
        lock (_gate)
        {
            if (wasHit)
                CacheHits++;
            else
                CacheMisses++;
        }
    }
    
    /// <summary>
    /// Resets all metrics to zero while preserving the service URL.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            TotalProbes = 0;
            SuccessfulProbes = 0;
            FailedProbes = 0;
            CacheHits = 0;
            CacheMisses = 0;
            Cancellations = 0;
            TotalDurationMs = 0;
            LastProbeAtUtc = DateTimeOffset.MinValue;
            LastSuccessAtUtc = DateTimeOffset.MinValue;
            LastError = null;
            ConsecutiveFailures = 0;
        }
    }
    
    /// <summary>
    /// Returns a summary string suitable for logging or diagnostics.
    /// </summary>
    public override string ToString()
    {
        lock (_gate)
        {
            return $"ServiceMetrics({ServiceUrl}): " +
                   $"Success={SuccessRate:F1}% ({SuccessfulProbes}/{TotalProbes}), " +
                   $"CacheHit={CacheHitRate:F1}% ({CacheHits}/{CacheHits + CacheMisses}), " +
                   $"AvgDuration={AverageDurationMs:F0}ms, " +
                   $"ConsecutiveFailures={ConsecutiveFailures}, " +
                   $"LastError={LastError ?? "<none>"}";
        }
    }
}

/// <summary>
/// Interface for components that need to report or consume probe metrics.
/// </summary>
public interface IProbeMetricsReporter
{
    /// <summary>
    /// Called when a probe operation completes (success, failure, or cancellation).
    /// </summary>
    void ReportProbeResult(string serviceUrl, ProbeResult result, TimeSpan? duration = null, bool wasCacheHit = false, string? errorDetail = null);
    
    /// <summary>
    /// Called when a cache access occurs without a probe execution.
    /// </summary>
    void ReportCacheAccess(string serviceUrl, bool wasHit);
    
    /// <summary>
    /// Gets the current metrics for all services.
    /// </summary>
    ServiceMetrics[] GetAllMetrics();
    
    /// <summary>
    /// Gets metrics for a specific service.
    /// </summary>
    ServiceMetrics? GetMetrics(string serviceUrl);
}

/// <summary>
/// Result type for probe operations reported to metrics.
/// </summary>
public enum ProbeResult
{
    Success,
    Failure,
    Cancellation
}
