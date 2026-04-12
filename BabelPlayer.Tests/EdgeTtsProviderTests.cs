using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for EdgeTtsProvider TTS generation using the persistent JSON worker pool.
/// </summary>
public sealed class EdgeTtsProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _translationJsonPath;
    private readonly string _outputAudioPath;
    private readonly string _stateFilePath;
    private readonly AppLog _log;

    public EdgeTtsProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"babel-edgetts-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _translationJsonPath = Path.Combine(_testDir, "translation.json");
        _outputAudioPath = Path.Combine(_testDir, "output.mp3");
        _stateFilePath = Path.Combine(_testDir, "worker-state.jsonl");
        _log = new AppLog(Path.Combine(_testDir, "test.log"));
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_testDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task GenerateTtsAsync_ThrowsFileNotFoundException_WhenTranslationJsonNotFound()
    {
        using var provider = new EdgeTtsProvider(_log);
        var request = new TtsRequest(
            "nonexistent.json",
            _outputAudioPath,
            "en-US-AriaNeural");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateTtsAsync_ThrowsArgumentException_WhenTranslationJsonPathNull()
    {
        using var provider = new EdgeTtsProvider(_log);
        var request = new TtsRequest(
            null!,
            _outputAudioPath,
            "en-US-AriaNeural");

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateTtsAsync_ThrowsArgumentException_WhenOutputAudioPathNull()
    {
        CreateSampleTranslationJson();
        using var provider = new EdgeTtsProvider(_log);
        var request = new TtsRequest(
            _translationJsonPath,
            null!,
            "en-US-AriaNeural");

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateTtsAsync_UsesSegmentWorkerPool()
    {
        CreateSampleTranslationJson();
        using var provider = CreateSimulatedProvider();

        var result = await provider.GenerateTtsAsync(
            new TtsRequest(_translationJsonPath, _outputAudioPath, "en-US-AriaNeural"));

        Assert.True(result.Success);
        Assert.Equal(_outputAudioPath, result.AudioPath);
        Assert.True(File.Exists(_outputAudioPath));

        var stateEntries = ReadStateEntries();
        Assert.Single(stateEntries);
        Assert.Equal("en-US-AriaNeural", stateEntries[0].Voice);
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_ThrowsArgumentException_WhenTextIsEmpty()
    {
        using var provider = new EdgeTtsProvider(_log);
        var request = new SingleSegmentTtsRequest(
            string.Empty,
            _outputAudioPath,
            "en-US-AriaNeural");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_ThrowsArgumentException_WhenTextIsWhitespace()
    {
        using var provider = new EdgeTtsProvider(_log);
        var request = new SingleSegmentTtsRequest(
            "   ",
            _outputAudioPath,
            "en-US-AriaNeural");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_ThrowsArgumentException_WhenOutputPathNull()
    {
        using var provider = new EdgeTtsProvider(_log);
        var request = new SingleSegmentTtsRequest(
            "Hello world",
            null!,
            "en-US-AriaNeural");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_UsesWorkerScriptPath()
    {
        using var provider = CreateSimulatedProvider();
        var result = await provider.GenerateSegmentTtsAsync(
            new SingleSegmentTtsRequest("Hello worker", _outputAudioPath, "en-US-AriaNeural"));

        Assert.True(result.Success);
        Assert.True(File.Exists(_outputAudioPath));
        Assert.Equal(new FileInfo(_outputAudioPath).Length, result.FileSizeBytes);

        var stateEntries = ReadStateEntries();
        Assert.Single(stateEntries);
        Assert.Equal("en-US-AriaNeural", stateEntries[0].Voice);
        Assert.Equal(_outputAudioPath, stateEntries[0].OutputPath);
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_ReusesPersistentWorkersAcrossRequests()
    {
        using var provider = CreateSimulatedProvider();

        for (var index = 0; index < 6; index++)
        {
            var outputPath = Path.Combine(_testDir, $"segment-{index}.mp3");
            var result = await provider.GenerateSegmentTtsAsync(
                new SingleSegmentTtsRequest($"Hello #{index}", outputPath, "en-US-AriaNeural"));

            Assert.True(result.Success);
            Assert.True(File.Exists(outputPath));
        }

        var stateEntries = ReadStateEntries();
        Assert.Equal(6, stateEntries.Count);
        Assert.InRange(stateEntries.Select(entry => entry.Pid).Distinct().Count(), 1, 4);
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_SupportsCancellation()
    {
        using var provider = new EdgeTtsProvider(_log);
        var request = new SingleSegmentTtsRequest(
            "This is a test",
            _outputAudioPath,
            "en-US-AriaNeural");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GenerateSegmentTtsAsync(request, cts.Token));
    }

    private EdgeTtsProvider CreateSimulatedProvider() =>
        new(
            _log,
            ResolvePythonExecutablePath(),
            ResolveWorkerScriptPath(),
            ["--simulate", "--state-file", _stateFilePath],
            ensureRuntimeReadyAsync: _ => Task.CompletedTask);

    private static string ResolvePythonExecutablePath()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "python",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-c", "import sys; print(sys.executable)" },
        }) ?? throw new InvalidOperationException("Failed to start python to resolve executable path.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Failed to resolve python executable path: {stderr}");

        return stdout.Trim();
    }

    private static string ResolveWorkerScriptPath()
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "inference", "workers", "edge_tts_worker.py");
        Assert.True(File.Exists(scriptPath), $"Worker script not copied to output: {scriptPath}");
        return scriptPath;
    }

    private List<WorkerStateEntry> ReadStateEntries()
    {
        if (!File.Exists(_stateFilePath))
            return [];

        return File.ReadAllLines(_stateFilePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<WorkerStateEntry>(line)!)
            .ToList();
    }

    private void CreateSampleTranslationJson()
    {
        var json = """
        {
          "sourceLanguage": "es",
          "targetLanguage": "en",
          "segments": [
            {
              "id": "segment_0.0",
              "start": 0.0,
              "end": 2.5,
              "text": "Hola mundo",
              "translatedText": "Hello world"
            }
          ]
        }
        """;
        File.WriteAllText(_translationJsonPath, json);
    }

    private sealed class WorkerStateEntry
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("pid")]
        public required int Pid { get; init; }

        [JsonPropertyName("voice")]
        public required string Voice { get; init; }

        [JsonPropertyName("output_path")]
        public required string OutputPath { get; init; }

        [JsonPropertyName("file_size_bytes")]
        public required long FileSizeBytes { get; init; }
    }
}
