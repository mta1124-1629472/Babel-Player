using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Composes <see cref="BenchmarkRunHarness"/>, <see cref="BenchmarkResultWriter"/>,
/// and <see cref="WerComputer"/> into a single entry-point that runs a full
/// benchmark pass against all clips in a dataset manifest and writes one result
/// file per clip.
///
/// Usage:
/// <code>
/// var orchestrator = new BenchmarkOrchestrator(log, snapshot);
/// await orchestrator.RunAsync(
///     manifestPath:  "test-assets/datasets/bp.dataset.local.dialogue.es-en.s.v1.0.0/manifest.json",
///     outputDir:     "benchmarks/results/faster-whisper-base-cpu",
///     provider:      fasterWhisperProvider,
///     settings:      appSettings,
///     matrixId:      "fw-base-cpu-int8",
///     warmupRuns:    1,
///     measuredRuns:  5,
///     cancellationToken: ct);
/// </code>
/// </summary>
public sealed class BenchmarkOrchestrator
{
    private readonly AppLog _log;
    private readonly HardwareSnapshot _hardware;

    public BenchmarkOrchestrator(AppLog log, HardwareSnapshot hardware)
    {
        _log      = log;
        _hardware = hardware;
    }

    // ── Dataset manifest schema (mirrors manifest.json) ──────────────────────

    private sealed record DatasetManifest(
        string DatasetId,
        string Description,
        string Version,
        string Language,
        int TotalClips,
        List<DatasetClip> Clips
    );

    private sealed record DatasetClip(
        string Id,
        string AudioFile,
        double DurationSeconds,
        int SampleRateHz,
        string ReferenceTranscript,
        string? ReferenceTranslationEn,
        string? Notes
    );

    // ── Per-run metrics returned by the provider delegate ────────────────────

    private sealed record TranscribeRunResult(
        long ElapsedMs,
        string Hypothesis,
        double PeakVramMb,
        double PeakRamMb
    );

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a full benchmark pass: one <see cref="BenchmarkRunHarness"/> loop per clip.
    /// Writes one JSON result file per clip to <paramref name="outputDir"/>.
    /// </summary>
    /// <param name="manifestPath">Path to the dataset <c>manifest.json</c>.</param>
    /// <param name="outputDir">Directory that will receive one <c>.json</c> result file per clip.</param>
    /// <param name="provider">Transcription provider under test.</param>
    /// <param name="settings">App settings forwarded to the provider for model/compute config.</param>
    /// <param name="matrixId">Benchmark matrix identifier written into result files (e.g. <c>"fw-base-cpu-int8"</c>).</param>
    /// <param name="warmupRuns">Number of warmup runs (excluded from aggregates). Default: 1.</param>
    /// <param name="measuredRuns">Number of measured runs included in aggregates. Default: 5.</param>
    public async Task RunAsync(
        string manifestPath,
        string outputDir,
        ITranscriptionProvider provider,
        AppSettings settings,
        string matrixId,
        int warmupRuns = 1,
        int measuredRuns = 5,
        CancellationToken cancellationToken = default)
    {
        _log.Info($"BenchmarkOrchestrator: loading manifest from {manifestPath}");

        var manifest = await LoadManifestAsync(manifestPath, cancellationToken);
        var datasetDir = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException($"Cannot determine directory for manifest: {manifestPath}");

        var environmentSnapshot = BuildEnvironmentSnapshot();
        var harness             = new BenchmarkRunHarness(_log, warmupRuns, measuredRuns);
        var writer              = new BenchmarkResultWriter(_log);

        Directory.CreateDirectory(outputDir);

        _log.Info($"BenchmarkOrchestrator: running {manifest.Clips.Count} clip(s) — " +
                  $"{warmupRuns} warmup + {measuredRuns} measured each.");

        foreach (var clip in manifest.Clips)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var audioPath = Path.Combine(datasetDir, clip.AudioFile);
            if (!File.Exists(audioPath))
            {
                _log.Warning($"BenchmarkOrchestrator: audio file not found — emitting placeholder result for clip '{clip.Id}': {audioPath}");

                var placeholderInputs = new BenchmarkResultWriter.BenchmarkInputs(
                    DatasetId:           manifest.DatasetId,
                    MatrixId:            matrixId,
                    AudioDurationSeconds: clip.DurationSeconds,
                    SampleRateHz:        clip.SampleRateHz,
                    Provider:            provider.GetType().Name,
                    Model:               settings.TranscriptionModel,
                    ComputeDevice:       settings.TranscriptionProfile.ToString().ToLowerInvariant());

                var placeholderPath = Path.Combine(outputDir, $"{clip.Id}_{matrixId}.json");
                await writer.WriteAsync(
                    outputPath:          placeholderPath,
                    environmentSnapshot: environmentSnapshot,
                    inputs:              placeholderInputs,
                    entries:             [],
                    limitations:         [$"audio_stub: file not found — {clip.AudioFile}"],
                    cancellationToken:   cancellationToken);

                _log.Info($"BenchmarkOrchestrator: placeholder result written → {placeholderPath}");
                continue;
            }

            _log.Info($"BenchmarkOrchestrator: benchmarking clip '{clip.Id}' ({clip.DurationSeconds:F1} s)");

            var outputJsonPath = Path.Combine(
                Path.GetTempPath(),
                $"bench_transcript_{clip.Id}_{Guid.NewGuid():N}.json");

            try
            {
                var entries = await harness.RunAsync(
                    audioDurationSeconds: clip.DurationSeconds,
                    runFn: async (runIndex, runType, ct) =>
                    {
                        var request = new TranscriptionRequest(
                            SourceAudioPath: audioPath,
                            OutputJsonPath:  outputJsonPath,
                            ModelName:       settings.TranscriptionModel,
                            CpuComputeType:  settings.TranscriptionCpuComputeType,
                            CpuThreads:      settings.TranscriptionCpuThreads,
                            NumWorkers:      settings.TranscriptionNumWorkers);

                        var (elapsedMs, hypothesis, peakVramMb, peakRamMb) =
                            await RunSingleAsync(provider, request, ct);

                        // Compute WER / CER against reference transcript when available
                        var wer = -1.0;
                        var cer = -1.0;
                        if (!string.IsNullOrWhiteSpace(clip.ReferenceTranscript) &&
                            !string.IsNullOrWhiteSpace(hypothesis))
                        {
                            wer = WerComputer.ComputeWer(clip.ReferenceTranscript, hypothesis);
                            cer = WerComputer.ComputeCer(clip.ReferenceTranscript, hypothesis);
                        }

                        return new BenchmarkRunHarness.RunMetrics(
                            LatencyMs:   elapsedMs,
                            PeakVramMb:  peakVramMb,
                            PeakRamMb:   peakRamMb,
                            Wer:         wer,
                            Cer:         cer);
                    },
                    cancellationToken: cancellationToken);

                var inputs = new BenchmarkResultWriter.BenchmarkInputs(
                    DatasetId:           manifest.DatasetId,
                    MatrixId:            matrixId,
                    AudioDurationSeconds: clip.DurationSeconds,
                    SampleRateHz:        clip.SampleRateHz,
                    Provider:            provider.GetType().Name,
                    Model:               settings.TranscriptionModel,
                    ComputeDevice:       settings.TranscriptionProfile.ToString().ToLowerInvariant());

                var resultPath = Path.Combine(outputDir, $"{clip.Id}_{matrixId}.json");
                await writer.WriteAsync(
                    outputPath:          resultPath,
                    environmentSnapshot: environmentSnapshot,
                    inputs:              inputs,
                    entries:             entries,
                    cancellationToken:   cancellationToken);

                _log.Info($"BenchmarkOrchestrator: result written → {resultPath}");
            }
            finally
            {
                if (File.Exists(outputJsonPath))
                    File.Delete(outputJsonPath);
            }
        }

        _log.Info("BenchmarkOrchestrator: all clips complete.");
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a single transcription call and extracts elapsed time, hypothesis text,
    /// and memory metrics from the result.
    /// </summary>
    private static async Task<TranscribeRunResult> RunSingleAsync(
        ITranscriptionProvider provider,
        TranscriptionRequest request,
        CancellationToken ct)
    {
        // Use a Stopwatch as a fallback in case the provider does not surface
        // ElapsedMs itself (e.g. cloud providers that don't go through
        // PythonSubprocessServiceBase).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await provider.TranscribeAsync(request, ct);
        sw.Stop();

        // Prefer the provider's own ElapsedMs when available
        var elapsedMs = result.ElapsedMs > 0 ? result.ElapsedMs : sw.ElapsedMilliseconds;

        // Flatten all segment text into a single hypothesis string for WER
        var hypothesis = result.Segments != null
            ? string.Join(" ", System.Linq.Enumerable.Select(result.Segments, s => s.Text?.Trim()))
            : string.Empty;

        return new TranscribeRunResult(
            ElapsedMs:   elapsedMs,
            Hypothesis:  hypothesis,
            PeakVramMb:  result.PeakVramMb,
            PeakRamMb:   result.PeakRamMb);
    }

    private BenchmarkResultWriter.BenchmarkEnvironmentSnapshot BuildEnvironmentSnapshot()
    {
        return new BenchmarkResultWriter.BenchmarkEnvironmentSnapshot(
            CpuName: _hardware.CpuName,
            RamMb:   (long)(_hardware.SystemRamGb * 1024),
            GpuName: _hardware.GpuName,
            VramMb:  _hardware.GpuVramMb ?? 0);
    }

    private static async Task<DatasetManifest> LoadManifestAsync(
        string path,
        CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Dataset manifest not found: {path}");

        var json = await File.ReadAllTextAsync(path, ct);
        var manifest = JsonSerializer.Deserialize<DatasetManifest>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
            });

        return manifest ?? throw new InvalidOperationException(
            $"Failed to deserialize dataset manifest: {path}");
    }
}
