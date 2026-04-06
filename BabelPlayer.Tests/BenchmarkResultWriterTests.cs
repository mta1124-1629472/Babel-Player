using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for <see cref="BenchmarkResultWriter"/> — aggregation logic, warmup
/// exclusion, edge cases (empty measured set, single element, sentinel values).
/// </summary>
public sealed class BenchmarkResultWriterTests : IDisposable
{
    private readonly string _dir;
    private readonly BenchmarkResultWriter _writer;

    private static readonly BenchmarkResultWriter.BenchmarkEnvironmentSnapshot DefaultEnv =
        new(CpuName: "Test CPU", RamMb: 16384, GpuName: null, VramMb: 0);

    public BenchmarkResultWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-brw-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _writer = new BenchmarkResultWriter(new AppLog(Path.Combine(_dir, "test.log")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private BenchmarkResultWriter.BenchmarkInputs MakeInputs(string matrixId = "test-matrix") =>
        new(DatasetId: "test-dataset", MatrixId: matrixId,
            AudioDurationSeconds: 10.0, SampleRateHz: 16000,
            Provider: "test-provider", Model: "test-model",
            ComputeDevice: "cpu");

    private static BenchmarkResultWriter.BenchmarkRunEntry Measured(int index, long ms,
        double wer = -1, double cer = -1) =>
        new(RunIndex: index, RunType: "measured", LatencyMs: ms,
            AudioDurationSeconds: 10.0, Wer: wer, Cer: cer);

    private static BenchmarkResultWriter.BenchmarkRunEntry Warmup(int index, long ms) =>
        new(RunIndex: index, RunType: "warmup", LatencyMs: ms,
            AudioDurationSeconds: 10.0);

    private async Task<JsonElement> WriteAndReadAsync(
        IReadOnlyList<BenchmarkResultWriter.BenchmarkRunEntry> entries,
        BenchmarkResultWriter.BenchmarkInputs? inputs = null)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        await _writer.WriteAsync(path, DefaultEnv, inputs ?? MakeInputs(), entries);
        var text = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    // ── Top-level schema structure ─────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_OutputFile_HasExpectedTopLevelKeys()
    {
        var doc = await WriteAndReadAsync([Measured(0, 1000)]);
        Assert.True(doc.TryGetProperty("run_batch_id", out _));
        Assert.True(doc.TryGetProperty("created_at", out _));
        Assert.True(doc.TryGetProperty("environment_snapshot", out _));
        Assert.True(doc.TryGetProperty("normalized_inputs", out _));
        Assert.True(doc.TryGetProperty("results", out _));
        Assert.True(doc.TryGetProperty("aggregates", out _));
        Assert.True(doc.TryGetProperty("limitations", out _));
    }

    [Fact]
    public async Task WriteAsync_NormalizedInputs_ContainsMatrixId()
    {
        var doc = await WriteAndReadAsync([Measured(0, 1000)], MakeInputs("my-matrix"));
        var matrixId = doc.GetProperty("normalized_inputs").GetProperty("matrix_id").GetString();
        Assert.Equal("my-matrix", matrixId);
    }

    [Fact]
    public async Task WriteAsync_Limitations_IsEmptyArray()
    {
        var doc = await WriteAndReadAsync([Measured(0, 1000)]);
        var limitations = doc.GetProperty("limitations");
        Assert.Equal(JsonValueKind.Array, limitations.ValueKind);
        Assert.Equal(0, limitations.GetArrayLength());
    }

    // ── Empty measured set (all warmups) ───────────────────────────────────────

    [Fact]
    public async Task WriteAsync_NoMeasuredEntries_AggregatesAreZero()
    {
        var entries = new[] { Warmup(0, 500), Warmup(1, 600) };
        var doc = await WriteAndReadAsync(entries);
        var agg = doc.GetProperty("aggregates");

        Assert.Equal(0.0, agg.GetProperty("mean_latency_ms").GetDouble());
        Assert.Equal(0, agg.GetProperty("min_latency_ms").GetInt64());
        Assert.Equal(0, agg.GetProperty("max_latency_ms").GetInt64());
        Assert.Equal(0, agg.GetProperty("p50_latency_ms").GetInt64());
        Assert.Equal(0, agg.GetProperty("p95_latency_ms").GetInt64());
        Assert.Equal(0.0, agg.GetProperty("mean_rtf").GetDouble());
    }

    [Fact]
    public async Task WriteAsync_NoMeasuredEntries_WerCerAreSentinel()
    {
        var entries = new[] { Warmup(0, 500) };
        var doc = await WriteAndReadAsync(entries);
        var agg = doc.GetProperty("aggregates");
        Assert.Equal(-1.0, agg.GetProperty("mean_wer").GetDouble());
        Assert.Equal(-1.0, agg.GetProperty("mean_cer").GetDouble());
    }

    // ── Single measured run ────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_SingleMeasuredEntry_PercentilesEqualLatency()
    {
        var doc = await WriteAndReadAsync([Measured(0, 1234)]);
        var agg = doc.GetProperty("aggregates");

        Assert.Equal(1234.0, agg.GetProperty("mean_latency_ms").GetDouble());
        Assert.Equal(1234, agg.GetProperty("min_latency_ms").GetInt64());
        Assert.Equal(1234, agg.GetProperty("max_latency_ms").GetInt64());
        Assert.Equal(1234, agg.GetProperty("p50_latency_ms").GetInt64());
        Assert.Equal(1234, agg.GetProperty("p95_latency_ms").GetInt64());
    }

    [Fact]
    public async Task WriteAsync_SingleMeasuredEntry_RtfComputedCorrectly()
    {
        // 1000 ms latency / 10.0 s audio = RTF 0.1
        var doc = await WriteAndReadAsync([Measured(0, 1000)]);
        var rtf = doc.GetProperty("aggregates").GetProperty("mean_rtf").GetDouble();
        Assert.Equal(0.1, rtf, precision: 4);
    }

    // ── Multiple measured runs ─────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_FiveMeasuredEntries_MeanIsCorrect()
    {
        var entries = new[]
        {
            Measured(0, 100), Measured(1, 200), Measured(2, 300),
            Measured(3, 400), Measured(4, 500),
        };
        var doc = await WriteAndReadAsync(entries);
        var mean = doc.GetProperty("aggregates").GetProperty("mean_latency_ms").GetDouble();
        Assert.Equal(300.0, mean);
    }

    [Fact]
    public async Task WriteAsync_FiveMeasuredEntries_MinMaxCorrect()
    {
        var entries = new[]
        {
            Measured(0, 100), Measured(1, 200), Measured(2, 300),
            Measured(3, 400), Measured(4, 500),
        };
        var doc = await WriteAndReadAsync(entries);
        var agg = doc.GetProperty("aggregates");
        Assert.Equal(100, agg.GetProperty("min_latency_ms").GetInt64());
        Assert.Equal(500, agg.GetProperty("max_latency_ms").GetInt64());
    }

    [Fact]
    public async Task WriteAsync_FiveMeasuredEntries_P50IsMedian()
    {
        // Sorted: 100, 200, 300, 400, 500 → ceiling(0.5×5)-1 = index 2 = 300
        var entries = new[]
        {
            Measured(0, 500), Measured(1, 100), Measured(2, 300),
            Measured(3, 200), Measured(4, 400),
        };
        var doc = await WriteAndReadAsync(entries);
        Assert.Equal(300, doc.GetProperty("aggregates").GetProperty("p50_latency_ms").GetInt64());
    }

    [Fact]
    public async Task WriteAsync_FiveMeasuredEntries_P95IsHighestWithSmallN()
    {
        // ceiling(0.95×5)-1 = ceiling(4.75)-1 = 5-1 = index 4 = 500
        var entries = new[]
        {
            Measured(0, 100), Measured(1, 200), Measured(2, 300),
            Measured(3, 400), Measured(4, 500),
        };
        var doc = await WriteAndReadAsync(entries);
        Assert.Equal(500, doc.GetProperty("aggregates").GetProperty("p95_latency_ms").GetInt64());
    }

    // ── Warmup exclusion ───────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_WarmupEntries_ExcludedFromAggregates()
    {
        // warmup = 9999 ms, measured = 100 ms → mean should be 100, not influenced by 9999
        var entries = new[]
        {
            Warmup(0, 9999),
            Measured(1, 100),
        };
        var doc = await WriteAndReadAsync(entries);
        var mean = doc.GetProperty("aggregates").GetProperty("mean_latency_ms").GetDouble();
        Assert.Equal(100.0, mean);
    }

    [Fact]
    public async Task WriteAsync_WarmupEntries_PresentInResultsArray()
    {
        var entries = new[] { Warmup(0, 500), Measured(1, 100) };
        var doc = await WriteAndReadAsync(entries);
        var results = doc.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
    }

    // ── WER / CER sentinel handling ────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_AllMeasuredHaveSentinelWer_MeanWerIsSentinel()
    {
        var entries = new[]
        {
            Measured(0, 100, wer: -1, cer: -1),
            Measured(1, 200, wer: -1, cer: -1),
        };
        var doc = await WriteAndReadAsync(entries);
        Assert.Equal(-1.0, doc.GetProperty("aggregates").GetProperty("mean_wer").GetDouble());
        Assert.Equal(-1.0, doc.GetProperty("aggregates").GetProperty("mean_cer").GetDouble());
    }

    [Fact]
    public async Task WriteAsync_MixedSentinelAndRealWer_AveragesOnlyRealValues()
    {
        // run 0: wer=-1 (no ground truth), run 1: wer=0.5 → mean = 0.5
        var entries = new[]
        {
            Measured(0, 100, wer: -1),
            Measured(1, 200, wer: 0.5),
        };
        var doc = await WriteAndReadAsync(entries);
        Assert.Equal(0.5, doc.GetProperty("aggregates").GetProperty("mean_wer").GetDouble());
    }

    [Fact]
    public async Task WriteAsync_TwoMeasuredWithWer_MeanIsAverage()
    {
        var entries = new[]
        {
            Measured(0, 100, wer: 0.2, cer: 0.1),
            Measured(1, 200, wer: 0.4, cer: 0.3),
        };
        var doc = await WriteAndReadAsync(entries);
        var agg = doc.GetProperty("aggregates");
        Assert.Equal(0.3, agg.GetProperty("mean_wer").GetDouble(), precision: 4);
        Assert.Equal(0.2, agg.GetProperty("mean_cer").GetDouble(), precision: 4);
    }

    // ── Directory creation ─────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_OutputDirectoryDoesNotExist_CreatesDirectoryAndFile()
    {
        var nestedPath = Path.Combine(_dir, "a", "b", "c", "result.json");
        await _writer.WriteAsync(nestedPath, DefaultEnv, MakeInputs(), [Measured(0, 100)]);
        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public async Task WriteAsync_OutputPathHasNoDirectory_WritesFileWithoutThrowing()
    {
        // Change to temp dir to avoid writing to current dir in real environment
        var originalDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_dir);
            var fileName = $"{Guid.NewGuid():N}.json";
            await _writer.WriteAsync(fileName, DefaultEnv, MakeInputs(), [Measured(0, 100)]);
            Assert.True(File.Exists(Path.Combine(_dir, fileName)));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }
}
