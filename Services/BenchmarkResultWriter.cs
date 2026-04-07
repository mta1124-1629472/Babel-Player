using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Serialises benchmark timing / quality data into the canonical benchmark JSON
/// schema used by the <c>benchmarks/</c> directory.
///
/// Responsibilities:
///   - Accept a batch of <see cref="BenchmarkRunEntry"/> objects (one per measured run)
///   - Compute aggregate statistics (mean, min, max, p50, p95)
///   - Write the result file to a caller-specified path
///
/// This service is intentionally stateless — callers own the list of entries
/// and call <see cref="WriteAsync"/> exactly once per benchmark batch.
/// </summary>
public sealed class BenchmarkResultWriter
{
    private readonly AppLog _log;

    public BenchmarkResultWriter(AppLog log) => _log = log;

    // ── Schema types ────────────────────────────────────────────────────────

    public sealed record BenchmarkRunEntry(
        /// <summary>0-based index of this run within the batch.</summary>
        int RunIndex,
        /// <summary>"warmup" or "measured".</summary>
        string RunType,
        /// <summary>Wall-clock latency in milliseconds.</summary>
        long LatencyMs,
        /// <summary>Audio duration processed, in seconds. Used to compute RTF.</summary>
        double AudioDurationSeconds,
        /// <summary>Peak VRAM in MB sampled during inference (-1 when unavailable).</summary>
        double PeakVramMb = -1,
        /// <summary>Peak RAM in MB sampled during inference (-1 when unavailable).</summary>
        double PeakRamMb = -1,
        /// <summary>Word Error Rate in [0,1] range (-1 when ground truth unavailable).</summary>
        double Wer = -1,
        /// <summary>Character Error Rate in [0,1] range (-1 when ground truth unavailable).</summary>
        double Cer = -1
    );

    /// <summary>
    /// Top-level benchmark result file. Top-level structure is intentionally aligned with
    /// the Python-emitted benchmark schema (<c>environment_snapshot</c>,
    /// <c>normalized_inputs</c>, <c>results</c>, <c>limitations</c>). The <c>aggregates</c>
    /// section is a C#-side addition and is not present in Python-emitted files.
    /// </summary>
    public sealed record BenchmarkResultFile(
        [property: JsonPropertyName("schema_version")]       string SchemaVersion,
        [property: JsonPropertyName("run_batch_id")]         string RunBatchId,
        [property: JsonPropertyName("created_at")]           string CreatedAt,
        [property: JsonPropertyName("environment_snapshot")] BenchmarkEnvironmentSnapshot EnvironmentSnapshot,
        [property: JsonPropertyName("normalized_inputs")]    BenchmarkInputs NormalizedInputs,
        [property: JsonPropertyName("results")]              List<BenchmarkResultEntry> Results,
        [property: JsonPropertyName("aggregates")]           BenchmarkAggregates Aggregates,
        [property: JsonPropertyName("limitations")]          List<string> Limitations
    );

    /// <summary>Hardware and runtime environment at the time of the benchmark run.</summary>
    public sealed record BenchmarkEnvironmentSnapshot(
        [property: JsonPropertyName("cpu_name")]    string? CpuName,
        [property: JsonPropertyName("ram_mb")]      long RamMb,
        [property: JsonPropertyName("gpu_name")]    string? GpuName,
        [property: JsonPropertyName("vram_mb")]     long VramMb
    );

    public sealed record BenchmarkInputs(
        [property: JsonPropertyName("dataset_id")]       string DatasetId,
        [property: JsonPropertyName("matrix_id")]        string MatrixId,
        [property: JsonPropertyName("audio_duration_s")] double AudioDurationSeconds,
        [property: JsonPropertyName("sample_rate_hz")]   int SampleRateHz,
        [property: JsonPropertyName("provider")]         string Provider,
        [property: JsonPropertyName("model")]            string Model,
        [property: JsonPropertyName("compute_device")]   string ComputeDevice
    );

    public sealed record BenchmarkResultEntry(
        [property: JsonPropertyName("run_index")]       int RunIndex,
        [property: JsonPropertyName("run_type")]        string RunType,
        [property: JsonPropertyName("latency_ms")]      long LatencyMs,
        [property: JsonPropertyName("rtf")]             double Rtf,
        [property: JsonPropertyName("peak_vram_mb")]    double PeakVramMb,
        [property: JsonPropertyName("peak_ram_mb")]     double PeakRamMb,
        [property: JsonPropertyName("wer")]             double Wer,
        [property: JsonPropertyName("cer")]             double Cer
    );

    public sealed record BenchmarkAggregates(
        [property: JsonPropertyName("mean_latency_ms")]  double MeanLatencyMs,
        [property: JsonPropertyName("min_latency_ms")]   long MinLatencyMs,
        [property: JsonPropertyName("max_latency_ms")]   long MaxLatencyMs,
        [property: JsonPropertyName("p50_latency_ms")]   long P50LatencyMs,
        [property: JsonPropertyName("p95_latency_ms")]   long P95LatencyMs,
        [property: JsonPropertyName("mean_rtf")]         double MeanRtf,
        [property: JsonPropertyName("mean_wer")]         double MeanWer,
        [property: JsonPropertyName("mean_cer")]         double MeanCer
    );

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a benchmark result file to <paramref name="outputPath"/>.
    /// Only <c>"measured"</c> entries are included in aggregate statistics;
    /// warmup entries are written to the results array but excluded from aggregates.
    /// </summary>
    public async Task WriteAsync(
        string outputPath,
        BenchmarkEnvironmentSnapshot environmentSnapshot,
        BenchmarkInputs inputs,
        IReadOnlyList<BenchmarkRunEntry> entries,
        IReadOnlyList<string>? limitations = null,
        CancellationToken cancellationToken = default)
    {
        var measured = new List<BenchmarkRunEntry>();
        var resultEntries = new List<BenchmarkResultEntry>();

        foreach (var e in entries)
        {
            var rtf = e.AudioDurationSeconds > 0
                ? e.LatencyMs / 1000.0 / e.AudioDurationSeconds
                : -1;

            resultEntries.Add(new BenchmarkResultEntry(
                e.RunIndex, e.RunType, e.LatencyMs,
                Math.Round(rtf, 4),
                e.PeakVramMb, e.PeakRamMb,
                e.Wer, e.Cer));

            if (e.RunType == "measured")
                measured.Add(e);
        }

        var aggregates = ComputeAggregates(measured, inputs.AudioDurationSeconds);

        var file = new BenchmarkResultFile(
            SchemaVersion:       "1.0",
            RunBatchId:          Guid.NewGuid().ToString("N"),
            CreatedAt:           DateTimeOffset.UtcNow.ToString("o"),
            EnvironmentSnapshot: environmentSnapshot,
            NormalizedInputs:    inputs,
            Results:             resultEntries,
            Aggregates:          aggregates,
            Limitations:         limitations != null ? new List<string>(limitations) : []);

        var json = JsonSerializer.Serialize(file, new JsonSerializerOptions
        {
            WriteIndented          = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        });

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        await File.WriteAllTextAsync(outputPath, json, cancellationToken);
        _log.Info($"Benchmark result written → {outputPath}");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static BenchmarkAggregates ComputeAggregates(
        IReadOnlyList<BenchmarkRunEntry> measured,
        double audioDurationSeconds)
    {
        if (measured.Count == 0)
        {
            return new BenchmarkAggregates(0, 0, 0, 0, 0, 0, -1, -1);
        }

        var latencies = new List<long>(measured.Count);
        double sumLatency = 0, sumWer = 0, sumCer = 0;
        int werCount = 0, cerCount = 0;

        foreach (var e in measured)
        {
            latencies.Add(e.LatencyMs);
            sumLatency += e.LatencyMs;
            if (e.Wer >= 0) { sumWer += e.Wer; werCount++; }
            if (e.Cer >= 0) { sumCer += e.Cer; cerCount++; }
        }

        latencies.Sort();
        var mean = sumLatency / measured.Count;
        var meanRtf = audioDurationSeconds > 0 ? mean / 1000.0 / audioDurationSeconds : -1;

        return new BenchmarkAggregates(
            MeanLatencyMs: Math.Round(mean, 2),
            MinLatencyMs:  latencies[0],
            MaxLatencyMs:  latencies[^1],
            P50LatencyMs:  Percentile(latencies, 0.50),
            P95LatencyMs:  Percentile(latencies, 0.95),
            MeanRtf:       Math.Round(meanRtf, 4),
            MeanWer:       werCount > 0 ? Math.Round(sumWer / werCount, 4) : -1,
            MeanCer:       cerCount > 0 ? Math.Round(sumCer / cerCount, 4) : -1
        );
    }

    private static long Percentile(List<long> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}
