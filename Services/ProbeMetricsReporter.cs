using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Service for reporting and exporting containerized probe metrics.
/// Provides multiple output formats for monitoring and diagnostics.
/// </summary>
public sealed class ProbeMetricsReporter(ContainerizedServiceProbe probe, AppLog log)
{
    private readonly ContainerizedServiceProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    private readonly AppLog _log = log ?? throw new ArgumentNullException(nameof(log));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Gets a human-readable summary of all probe metrics.
    /// </summary>
    public string GetSummaryReport()
    {
        var metrics = _probe.GetAllMetrics();
        if (metrics.Length == 0)
            return "No probe metrics available.";

        var summary = new System.Text.StringBuilder();
        summary.AppendLine("=== Containerized Service Probe Metrics ===");
        summary.AppendLine($"Report generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss UTC}");
        summary.AppendLine($"Total services monitored: {metrics.Length}");
        summary.AppendLine();

        foreach (var metric in metrics)
        {
            summary.AppendLine($"Service: {metric.ServiceUrl}");
            summary.AppendLine($"  Success Rate: {metric.SuccessRate:F1}% ({metric.SuccessfulProbes}/{metric.TotalProbes})");
            summary.AppendLine($"  Cache Hit Rate: {metric.CacheHitRate:F1}% ({metric.CacheHits}/{metric.CacheHits + metric.CacheMisses})");
            summary.AppendLine($"  Average Duration: {metric.AverageDurationMs:F0}ms");
            summary.AppendLine($"  Consecutive Failures: {metric.ConsecutiveFailures}");
            summary.AppendLine($"  Last Probe: {metric.LastProbeAtUtc:yyyy-MM-dd HH:mm:ss UTC}");
            summary.AppendLine($"  Last Success: {(metric.LastSuccessAtUtc == DateTimeOffset.MinValue ? "Never" : metric.LastSuccessAtUtc.ToString("yyyy-MM-dd HH:mm:ss UTC"))}");
            summary.AppendLine($"  Last Error: {metric.LastError ?? "<none>"}");
            summary.AppendLine();
        }

        return summary.ToString();
    }

    /// <summary>
    /// Exports metrics as JSON for programmatic consumption.
    /// </summary>
    public Task<string> ExportJsonAsync()
    {
        var metrics = _probe.GetAllMetrics();
        var exportData = new
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            ServiceCount = metrics.Length,
            Services = metrics.Select(m => new
            {
                m.ServiceUrl,
                m.TotalProbes,
                m.SuccessfulProbes,
                m.FailedProbes,
                m.Cancellations,
                m.CacheHits,
                m.CacheMisses,
                m.SuccessRate,
                m.CacheHitRate,
                m.AverageDurationMs,
                m.ConsecutiveFailures,
                LastProbeAtUtc = m.LastProbeAtUtc == DateTimeOffset.MinValue ? (DateTimeOffset?)null : m.LastProbeAtUtc,
                LastSuccessAtUtc = m.LastSuccessAtUtc == DateTimeOffset.MinValue ? (DateTimeOffset?)null : m.LastSuccessAtUtc,
                m.LastError
            })
        };

        return Task.FromResult(JsonSerializer.Serialize(exportData, JsonOptions));
    }

    /// <summary>
    /// Exports metrics as CSV for spreadsheet analysis.
    /// </summary>
    public string ExportCsv()
    {
        var metrics = _probe.GetAllMetrics();
        if (metrics.Length == 0)
            return "ServiceUrl,TotalProbes,SuccessfulProbes,FailedProbes,Cancellations,CacheHits,CacheMisses,SuccessRate,CacheHitRate,AverageDurationMs,ConsecutiveFailures,LastProbeAtUtc,LastSuccessAtUtc,LastError";

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("ServiceUrl,TotalProbes,SuccessfulProbes,FailedProbes,Cancellations,CacheHits,CacheMisses,SuccessRate,CacheHitRate,AverageDurationMs,ConsecutiveFailures,LastProbeAtUtc,LastSuccessAtUtc,LastError");

        foreach (var metric in metrics)
        {
            csv.AppendLine($"{EscapeCsv(metric.ServiceUrl)}," +
                         $"{metric.TotalProbes}," +
                         $"{metric.SuccessfulProbes}," +
                         $"{metric.FailedProbes}," +
                         $"{metric.Cancellations}," +
                         $"{metric.CacheHits}," +
                         $"{metric.CacheMisses}," +
                         $"{metric.SuccessRate:F2}," +
                         $"{metric.CacheHitRate:F2}," +
                         $"{metric.AverageDurationMs:F2}," +
                         $"{metric.ConsecutiveFailures}," +
                         $"{(metric.LastProbeAtUtc == DateTimeOffset.MinValue ? "" : metric.LastProbeAtUtc.ToString("yyyy-MM-dd HH:mm:ss UTC"))}," +
                         $"{(metric.LastSuccessAtUtc == DateTimeOffset.MinValue ? "" : metric.LastSuccessAtUtc.ToString("yyyy-MM-dd HH:mm:ss UTC"))}," +
                         $"{EscapeCsv(metric.LastError ?? "")}");
        }

        return csv.ToString();
    }

    /// <summary>
    /// Writes metrics to a file in the specified format.
    /// </summary>
    public async Task WriteToFileAsync(string filePath, MetricsFormat format, CancellationToken cancellationToken = default)
    {
        var content = format switch
        {
            MetricsFormat.Text => GetSummaryReport(),
            MetricsFormat.Json => await ExportJsonAsync(),
            MetricsFormat.Csv => ExportCsv(),
            _ => throw new ArgumentException($"Unsupported format: {format}")
        };

        await File.WriteAllTextAsync(filePath, content, cancellationToken);
        _log.Info($"Probe metrics exported to {filePath} in {format} format");
    }

    /// <summary>
    /// Logs a concise summary to the application log.
    /// </summary>
    public void LogSummary()
    {
        var metrics = _probe.GetAllMetrics();
        if (metrics.Length == 0)
        {
            _log.Info("Probe metrics: No services monitored");
            return;
        }

        var totalProbes = metrics.Sum(m => m.TotalProbes);
        var totalSuccesses = metrics.Sum(m => m.SuccessfulProbes);
        var overallSuccessRate = totalProbes > 0 ? (double)totalSuccesses / totalProbes * 100 : 0;
        var totalCacheHits = metrics.Sum(m => m.CacheHits);
        var totalCacheAccesses = metrics.Sum(m => m.CacheHits + m.CacheMisses);
        var overallCacheHitRate = totalCacheAccesses > 0 ? (double)totalCacheHits / totalCacheAccesses * 100 : 0;

        _log.Info($"Probe metrics summary: {metrics.Length} services, " +
                 $"{totalProbes} total probes, {overallSuccessRate:F1}% success rate, " +
                 $"{overallCacheHitRate:F1}% cache hit rate");

        // Log services with consecutive failures
        var failingServices = metrics.Where(m => m.ConsecutiveFailures > 0).ToArray();
        if (failingServices.Length > 0)
        {
            _log.Warning($"Services with consecutive failures: {failingServices.Length}");
            foreach (var failing in failingServices)
            {
                _log.Warning($"  {failing.ServiceUrl}: {failing.ConsecutiveFailures} failures, last error: {failing.LastError ?? "<none>"}");
            }
        }
    }

    /// <summary>
    /// Gets metrics for services that might need attention (high failure rates, consecutive failures, etc.).
    /// </summary>
    public ServiceMetrics[] GetProblematicServices(double failureRateThreshold = 50.0, int consecutiveFailureThreshold = 3)
    {
        return [.. _probe.GetAllMetrics()
            .Where(m => 
                (m.TotalProbes >= 5 && m.SuccessRate < failureRateThreshold) ||
                m.ConsecutiveFailures >= consecutiveFailureThreshold)];
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}

/// <summary>
/// Export format options for probe metrics.
/// </summary>
public enum MetricsFormat
{
    Text,
    Json,
    Csv
}
