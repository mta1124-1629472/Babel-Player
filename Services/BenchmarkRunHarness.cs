using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Executes a benchmark in a warmup + measured run loop, collecting
/// <see cref="BenchmarkResultWriter.BenchmarkRunEntry"/> objects for every run.
///
/// Usage:
/// <code>
/// var harness = new BenchmarkRunHarness(log, warmupRuns: 1, measuredRuns: 5);
/// var entries = await harness.RunAsync(
///     audioDurationSeconds: 30.0,
///     runFn: async (runIndex, runType, ct) =>
///     {
///         var result = await provider.TranscribeAsync(request, ct);
///         return new RunMetrics(result.ElapsedMs, peakVramMb: -1, peakRamMb: -1,
///                               wer: -1, cer: -1);
///     },
///     cancellationToken: ct);
/// </code>
/// </summary>
public sealed class BenchmarkRunHarness
{
    private readonly AppLog _log;
    private readonly int _warmupRuns;
    private readonly int _measuredRuns;

    public BenchmarkRunHarness(AppLog log, int warmupRuns = 1, int measuredRuns = 5)
    {
        _log          = log;
        _warmupRuns   = warmupRuns   < 0 ? 0 : warmupRuns;
        _measuredRuns = measuredRuns < 1 ? 1 : measuredRuns;
    }

    /// <summary>Timing + quality data returned by each invocation of the run function.</summary>
    public sealed record RunMetrics(
        long LatencyMs,
        double PeakVramMb = -1,
        double PeakRamMb  = -1,
        double Wer        = -1,
        double Cer        = -1
    );

    /// <summary>
    /// Executes <paramref name="runFn"/> for each warmup run then each measured run,
    /// returning a flat list of <see cref="BenchmarkResultWriter.BenchmarkRunEntry"/> objects.
    ///
    /// Warmup runs are labelled <c>"warmup"</c>; measured runs are labelled <c>"measured"</c>.
    /// Run indices are 0-based and monotonically increasing across both phases.
    /// </summary>
    /// <param name="audioDurationSeconds">Used to compute RTF in the result writer.</param>
    /// <param name="runFn">
    /// Async delegate invoked for each run. Receives (runIndex, runType, cancellationToken)
    /// and must return a <see cref="RunMetrics"/> record.
    /// </param>
    public async Task<IReadOnlyList<BenchmarkResultWriter.BenchmarkRunEntry>> RunAsync(
        double audioDurationSeconds,
        Func<int, string, CancellationToken, Task<RunMetrics>> runFn,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<BenchmarkResultWriter.BenchmarkRunEntry>();
        var runIndex = 0;

        // ── Warmup phase ────────────────────────────────────────────────────
        for (int i = 0; i < _warmupRuns; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _log.Info($"Benchmark warmup run {i + 1}/{_warmupRuns} (index {runIndex})");

            var metrics = await runFn(runIndex, "warmup", cancellationToken);

            entries.Add(new BenchmarkResultWriter.BenchmarkRunEntry(
                RunIndex:             runIndex++,
                RunType:              "warmup",
                LatencyMs:            metrics.LatencyMs,
                AudioDurationSeconds: audioDurationSeconds,
                PeakVramMb:           metrics.PeakVramMb,
                PeakRamMb:            metrics.PeakRamMb,
                Wer:                  metrics.Wer,
                Cer:                  metrics.Cer));

            _log.Info($"  warmup  run {i + 1}: {metrics.LatencyMs} ms");
        }

        // ── Measured phase ──────────────────────────────────────────────────
        var measuredLatencies = new List<long>(_measuredRuns);
        for (int i = 0; i < _measuredRuns; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _log.Info($"Benchmark measured run {i + 1}/{_measuredRuns} (index {runIndex})");

            var metrics = await runFn(runIndex, "measured", cancellationToken);
            measuredLatencies.Add(metrics.LatencyMs);

            entries.Add(new BenchmarkResultWriter.BenchmarkRunEntry(
                RunIndex:             runIndex++,
                RunType:              "measured",
                LatencyMs:            metrics.LatencyMs,
                AudioDurationSeconds: audioDurationSeconds,
                PeakVramMb:           metrics.PeakVramMb,
                PeakRamMb:            metrics.PeakRamMb,
                Wer:                  metrics.Wer,
                Cer:                  metrics.Cer));

            _log.Info($"  measured run {i + 1}: {metrics.LatencyMs} ms");
        }

        if (measuredLatencies.Count > 0)
        {
            var mean = 0L;
            foreach (var v in measuredLatencies) mean += v;
            _log.Info($"Benchmark complete — mean measured latency: {mean / measuredLatencies.Count} ms over {measuredLatencies.Count} run(s)");
        }

        return entries;
    }
}
