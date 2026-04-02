using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for PiperTtsProvider � TTS generation using Piper.
/// </summary>
public sealed class PiperTtsProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _translationJsonPath;
    private readonly string _outputAudioPath;
    private readonly string _modelDir;
    private readonly AppLog _log;

    public PiperTtsProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"babel-piper-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _modelDir = Path.Combine(_testDir, "models");
        Directory.CreateDirectory(_modelDir);
        _translationJsonPath = Path.Combine(_testDir, "translation.json");
        _outputAudioPath = Path.Combine(_testDir, "output.wav");
        _log = new AppLog(Path.Combine(_testDir, "test.log"));
    }

    public void Dispose()
    {
        _log?.Dispose();
        try { Directory.Delete(_testDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ?? Constructor ????????????????????????????????????????????????????????????

    [Fact]
    public void Constructor_AcceptsValidParameters()
    {
        var provider = new PiperTtsProvider(_log, _modelDir);
        Assert.NotNull(provider);
    }

    [Fact]
    public void Constructor_AcceptsNullModelDir()
    {
        var provider = new PiperTtsProvider(_log, null!);
        Assert.NotNull(provider);
    }

    // ?? GenerateTtsAsync ???????????????????????????????????????????????????????

    [Fact]
    public async Task GenerateTtsAsync_ThrowsFileNotFoundException_WhenTranslationJsonNotFound()
    {
        var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new TtsRequest(
            "nonexistent.json",
            _outputAudioPath,
            "en_US-lessac-medium");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateTtsAsync_ThrowsArgumentException_WhenTranslationJsonPathNull()
    {
        var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new TtsRequest(
            null!,
            _outputAudioPath,
            "en_US-lessac-medium");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateTtsAsync_ThrowsArgumentException_WhenOutputAudioPathNull()
    {
        CreateSampleTranslationJson();
        var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new TtsRequest(
            _translationJsonPath,
            null!,
            "en_US-lessac-medium");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GenerateTtsAsync(request));
    }

    // ?? GenerateSegmentTtsAsync ????????????????????????????????????????????????

    [Fact]
    public async Task GenerateSegmentTtsAsync_ThrowsArgumentException_WhenTextIsEmpty()
    {
        var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new SingleSegmentTtsRequest(
            string.Empty,
            _outputAudioPath,
            "en_US-lessac-medium");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_ThrowsArgumentException_WhenTextIsWhitespace()
    {
        var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new SingleSegmentTtsRequest(
            "   ",
            _outputAudioPath,
            "en_US-lessac-medium");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_ThrowsArgumentException_WhenOutputPathNull()
    {
        var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new SingleSegmentTtsRequest(
            "Hello world",
            null!,
            "en_US-lessac-medium");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_SupportsCancellation()
    {
        var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new SingleSegmentTtsRequest(
            "This is a test",
            _outputAudioPath,
            "en_US-lessac-medium");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GenerateSegmentTtsAsync(request, cts.Token));
    }

    // ?? CheckReadiness ?????????????????????????????????????????????????????????

    [Fact]
    public void CheckReadiness_ReturnsNotReady_WhenVoiceNotDownloaded()
    {
        var provider = new PiperTtsProvider(_log, _modelDir);
        var settings = new Babel.Player.Services.Settings.AppSettings
        {
            TtsVoice = "nonexistent-voice",
            PiperModelDir = _modelDir
        };

        var readiness = provider.CheckReadiness(settings);

        Assert.False(readiness.IsReady);
        Assert.True(readiness.RequiresModelDownload);
        Assert.Contains("not downloaded", readiness.BlockingReason);
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
