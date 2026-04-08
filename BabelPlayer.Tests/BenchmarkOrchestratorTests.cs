using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Tests;

/// <summary>
/// Unit tests for <see cref="BenchmarkOrchestrator"/>.
/// Uses a fake <see cref="ITranscriptionProvider"/> so no Python process is spawned.
/// </summary>
[Collection("SequentialTests")]
public sealed class BenchmarkOrchestratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppLog _log;
    private readonly HardwareSnapshot _fakeHardware;

    public BenchmarkOrchestratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bp_orch_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _log = new AppLog(Path.Combine(_tempDir, "bench-test.log"));
        _fakeHardware = HardwareSnapshot.Detecting with
        {
            IsDetecting    = false,
            CpuName        = "Test CPU",
            CpuCores       = 8,
            SystemRamGb    = 16,
            GpuName        = null,
            GpuVramMb      = null,
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Ignore transient cleanup failures in test teardown.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore transient cleanup failures in test teardown.
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a minimal manifest.json plus real (silent) WAV stubs so the
    /// orchestrator can actually open the files.
    /// </summary>
    private string WriteManifest(string datasetDir, IEnumerable<(string id, string text)> clips)
    {
        Directory.CreateDirectory(datasetDir);

        var clipList = new System.Text.StringBuilder();
        bool first = true;
        foreach (var (id, text) in clips)
        {
            if (!first) clipList.Append(',');
            first = false;

            // Write a minimal valid 16 kHz mono WAV stub (44-byte header, 0 samples)
            var wavPath = Path.Combine(datasetDir, $"{id}.wav");
            WriteMinimalWav(wavPath);

            clipList.Append($@"
            {{
                ""id"": ""{id}"",
                ""audio_file"": ""{id}.wav"",
                ""duration_seconds"": 2.0,
                ""sample_rate_hz"": 16000,
                ""reference_transcript"": ""{text}"",
                ""reference_translation_en"": null,
                ""notes"": null
            }}");
        }

        var manifest = $@"{{
            ""dataset_id"": ""test-dataset"",
            ""description"": ""unit-test dataset"",
            ""version"": ""0.0.0"",
            ""language"": ""es"",
            ""total_clips"": 1,
            ""clips"": [{clipList}]
        }}";

        var manifestPath = Path.Combine(datasetDir, "manifest.json");
        File.WriteAllText(manifestPath, manifest);
        return manifestPath;
    }

    /// <summary>44-byte PCM WAV header, 0 samples of data.</summary>
    private static void WriteMinimalWav(string path)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        // RIFF header
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write((uint)36);       // chunk size (header - 8)
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        // fmt chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write((uint)16);       // subchunk size
        bw.Write((ushort)1);      // PCM
        bw.Write((ushort)1);      // mono
        bw.Write((uint)16000);    // sample rate
        bw.Write((uint)32000);    // byte rate (16000 * 1 * 2)
        bw.Write((ushort)2);      // block align
        bw.Write((ushort)16);     // bits per sample
        // data chunk (empty)
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write((uint)0);
    }

    private AppSettings MakeSettings(string model = "tiny") => new()
    {
        TranscriptionModel          = model,
        TranscriptionCpuComputeType = "int8",
        TranscriptionCpuThreads     = 0,
        TranscriptionNumWorkers     = 1,
    };

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ManifestNotFound_ThrowsFileNotFoundException()
    {
        var orchestrator = new BenchmarkOrchestrator(_log, _fakeHardware);
        var outputDir    = Path.Combine(_tempDir, "out");
        var fakeProvider = new FakeTranscriptionProvider();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            orchestrator.RunAsync(
                manifestPath:  Path.Combine(_tempDir, "does_not_exist", "manifest.json"),
                outputDir:     outputDir,
                provider:      fakeProvider,
                settings:      MakeSettings(),
                matrixId:      "test-matrix",
                warmupRuns:    0,
                measuredRuns:  1));
    }

    [Fact]
    public async Task RunAsync_MissingAudioFile_SkipsClipGracefully()
    {
        var datasetDir = Path.Combine(_tempDir, "dataset_skip");
        Directory.CreateDirectory(datasetDir);

        // Write manifest referencing a non-existent WAV
        var manifestJson = @"{
            ""dataset_id"": ""test"",
            ""description"": """",
            ""version"": ""0"",
            ""language"": ""es"",
            ""total_clips"": 1,
            ""clips"": [{
                ""id"": ""missing_clip"",
                ""audio_file"": ""does_not_exist.wav"",
                ""duration_seconds"": 5.0,
                ""sample_rate_hz"": 16000,
                ""reference_transcript"": ""hola"",
                ""reference_translation_en"": null,
                ""notes"": null
            }]
        }";
        var manifestPath = Path.Combine(datasetDir, "manifest.json");
        File.WriteAllText(manifestPath, manifestJson);

        var outputDir    = Path.Combine(_tempDir, "out_skip");
        var fakeProvider = new FakeTranscriptionProvider();
        var orchestrator = new BenchmarkOrchestrator(_log, _fakeHardware);

        // Should complete without throwing; no result file written
        await orchestrator.RunAsync(
            manifestPath:  manifestPath,
            outputDir:     outputDir,
            provider:      fakeProvider,
            settings:      MakeSettings(),
            matrixId:      "test-matrix",
            warmupRuns:    0,
            measuredRuns:  1);

        // Orchestrator writes a placeholder result JSON for missing audio
        var files = Directory.Exists(outputDir)
            ? Directory.GetFiles(outputDir, "*.json")
            : Array.Empty<string>();
        var placeholderFile = Assert.Single(files);

        // Verify the placeholder captures the audio_stub limitation
        var json = await File.ReadAllTextAsync(placeholderFile);
        Assert.Contains("audio_stub", json);
    }

    [Fact]
    public async Task RunAsync_ValidClip_WritesResultJsonToOutputDir()
    {
        var datasetDir   = Path.Combine(_tempDir, "dataset_valid");
        var manifestPath = WriteManifest(datasetDir, [("clip_01", "hola mundo")]);
        var outputDir    = Path.Combine(_tempDir, "out_valid");

        var fakeProvider = new FakeTranscriptionProvider(
            hypothesis: "hola mundo",
            elapsedMs:  300);
        var orchestrator = new BenchmarkOrchestrator(_log, _fakeHardware);

        await orchestrator.RunAsync(
            manifestPath:  manifestPath,
            outputDir:     outputDir,
            provider:      fakeProvider,
            settings:      MakeSettings(),
            matrixId:      "test-matrix",
            warmupRuns:    0,
            measuredRuns:  1);

        var resultFiles = Directory.GetFiles(outputDir, "*.json");
        Assert.Single(resultFiles);
    }

    [Fact]
    public async Task RunAsync_ValidClip_ResultContainsNonNegativeLatency()
    {
        var datasetDir   = Path.Combine(_tempDir, "dataset_latency");
        var manifestPath = WriteManifest(datasetDir, [("clip_lat", "prueba")]);
        var outputDir    = Path.Combine(_tempDir, "out_latency");

        var fakeProvider = new FakeTranscriptionProvider(
            hypothesis: "prueba",
            elapsedMs:  500);
        var orchestrator = new BenchmarkOrchestrator(_log, _fakeHardware);

        await orchestrator.RunAsync(
            manifestPath:  manifestPath,
            outputDir:     outputDir,
            provider:      fakeProvider,
            settings:      MakeSettings(),
            matrixId:      "lat-matrix",
            warmupRuns:    0,
            measuredRuns:  2);

        var resultFile = Assert.Single(Directory.GetFiles(outputDir, "*.json"));
        var json       = await File.ReadAllTextAsync(resultFile);
        using var doc  = JsonDocument.Parse(json);

        // Verify results array has at least one entry with latency_ms >= 0
        var results = doc.RootElement.GetProperty("results");
        Assert.True(results.GetArrayLength() >= 1);
        foreach (var entry in results.EnumerateArray())
        {
            var latency = entry.GetProperty("latency_ms").GetDouble();
            Assert.True(latency >= 0, $"Expected latency_ms >= 0 but got {latency}");
        }
    }

    [Fact]
    public async Task RunAsync_PerfectHypothesis_WerIsZero()
    {
        var datasetDir   = Path.Combine(_tempDir, "dataset_wer");
        var manifestPath = WriteManifest(datasetDir, [("clip_wer", "hola como estas")]);
        var outputDir    = Path.Combine(_tempDir, "out_wer");

        // Hypothesis exactly matches reference (after normalization)
        var fakeProvider = new FakeTranscriptionProvider(hypothesis: "hola como estas");
        var orchestrator = new BenchmarkOrchestrator(_log, _fakeHardware);

        await orchestrator.RunAsync(
            manifestPath:  manifestPath,
            outputDir:     outputDir,
            provider:      fakeProvider,
            settings:      MakeSettings(),
            matrixId:      "wer-matrix",
            warmupRuns:    0,
            measuredRuns:  1);

        var resultFile = Assert.Single(Directory.GetFiles(outputDir, "*.json"));
        var json       = await File.ReadAllTextAsync(resultFile);
        using var doc  = JsonDocument.Parse(json);

        var aggregates = doc.RootElement.GetProperty("aggregates");
        var meanWer    = aggregates.GetProperty("mean_wer").GetDouble();
        Assert.Equal(0.0, meanWer, precision: 4);
    }

    [Fact]
    public async Task RunAsync_MultipleClips_WritesOneFilePerClip()
    {
        var datasetDir   = Path.Combine(_tempDir, "dataset_multi");
        var manifestPath = WriteManifest(datasetDir,
        [
            ("clip_a", "hola"),
            ("clip_b", "adios"),
            ("clip_c", "gracias"),
        ]);
        var outputDir    = Path.Combine(_tempDir, "out_multi");
        var fakeProvider = new FakeTranscriptionProvider(hypothesis: "hola");
        var orchestrator = new BenchmarkOrchestrator(_log, _fakeHardware);

        await orchestrator.RunAsync(
            manifestPath:  manifestPath,
            outputDir:     outputDir,
            provider:      fakeProvider,
            settings:      MakeSettings(),
            matrixId:      "multi-matrix",
            warmupRuns:    0,
            measuredRuns:  1);

        var resultFiles = Directory.GetFiles(outputDir, "*.json");
        Assert.Equal(3, resultFiles.Length);
    }

    [Fact]
    public async Task RunAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var datasetDir   = Path.Combine(_tempDir, "dataset_cancel");
        var manifestPath = WriteManifest(datasetDir, [("clip_x", "test")]);
        var outputDir    = Path.Combine(_tempDir, "out_cancel");

        using var cts    = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        var fakeProvider = new FakeTranscriptionProvider();
        var orchestrator = new BenchmarkOrchestrator(_log, _fakeHardware);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.RunAsync(
                manifestPath:  manifestPath,
                outputDir:     outputDir,
                provider:      fakeProvider,
                settings:      MakeSettings(),
                matrixId:      "cancel-matrix",
                warmupRuns:    0,
                measuredRuns:  1,
                cancellationToken: cts.Token));
    }

    // ── Fake provider ──────────────────────────────────────────────────────

    private sealed class FakeTranscriptionProvider : ITranscriptionProvider
    {
        private readonly string _hypothesis;
        private readonly long   _elapsedMs;

        public FakeTranscriptionProvider(
            string hypothesis = "test hypothesis",
            long elapsedMs    = 100)
        {
            _hypothesis = hypothesis;
            _elapsedMs  = elapsedMs;
        }

        public Task<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Write a minimal output JSON that ArtifactJson.LoadTranscriptAsync would accept
            var outputJson = $@"{{
                ""language"": ""es"",
                ""language_probability"": 0.98,
                ""peak_ram_mb"": 128.0,
                ""peak_vram_mb"": -1.0,
                ""segments"": [
                    {{ ""start"": 0.0, ""end"": 2.0, ""text"": "" {_hypothesis}"" }}
                ]
            }}";
            File.WriteAllText(request.OutputJsonPath, outputJson);

            var segments = new List<TranscriptSegment>
            {
                new TranscriptSegment(0.0, 2.0, _hypothesis)
            };

            return Task.FromResult(new TranscriptionResult(
                Success:             true,
                Segments:            segments,
                Language:            "es",
                LanguageProbability: 0.98,
                ErrorMessage:        null,
                ElapsedMs:           _elapsedMs,
                PeakVramMb:          -1,
                PeakRamMb:           128));
        }

        public ProviderReadiness CheckReadiness(
            AppSettings settings,
            Babel.Player.Services.Credentials.ApiKeyStore? keyStore = null)
            => ProviderReadiness.Ready;

        public Task<bool> EnsureReadyAsync(
            AppSettings settings,
            IProgress<double>? progress,
            CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
