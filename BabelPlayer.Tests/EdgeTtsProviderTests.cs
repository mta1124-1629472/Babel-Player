using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for EdgeTtsProvider � TTS generation using edge-tts.
/// </summary>
public sealed class EdgeTtsProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _translationJsonPath;
    private readonly string _outputAudioPath;
    private readonly AppLog _log;

    public EdgeTtsProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"babel-edgetts-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _translationJsonPath = Path.Combine(_testDir, "translation.json");
        _outputAudioPath = Path.Combine(_testDir, "output.mp3");
        _log = new AppLog(Path.Combine(_testDir, "test.log"));
    }

    public void Dispose()
    {
        _log?.Dispose();
        try { Directory.Delete(_testDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ?? GenerateTtsAsync ???????????????????????????????????????????????????????

    [Fact]
    public async Task GenerateTtsAsync_ThrowsFileNotFoundException_WhenTranslationJsonNotFound()
    {
        var provider = new EdgeTtsProvider(_log);
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
        var provider = new EdgeTtsProvider(_log);
        var request = new TtsRequest(
            null!,
            _outputAudioPath,
            "en-US-AriaNeural");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateTtsAsync_ThrowsArgumentException_WhenOutputAudioPathNull()
    {
        CreateSampleTranslationJson();
        var provider = new EdgeTtsProvider(_log);
        var request = new TtsRequest(
            _translationJsonPath,
            null!,
            "en-US-AriaNeural");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GenerateTtsAsync(request));
    }

    // ?? GenerateSegmentTtsAsync ????????????????????????????????????????????????

    [Fact]
    public async Task GenerateSegmentTtsAsync_ThrowsArgumentException_WhenTextIsEmpty()
    {
        var provider = new EdgeTtsProvider(_log);
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
        var provider = new EdgeTtsProvider(_log);
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
        var provider = new EdgeTtsProvider(_log);
        var request = new SingleSegmentTtsRequest(
            "Hello world",
            null!,
            "en-US-AriaNeural");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_SupportsCancellation()
    {
        var provider = new EdgeTtsProvider(_log);
        var request = new SingleSegmentTtsRequest(
            "This is a test",
            _outputAudioPath,
            "en-US-AriaNeural");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.GenerateSegmentTtsAsync(request, cts.Token));
    }

    // ?? Helper methods ?????????????????????????????????????????????????????????

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
}
