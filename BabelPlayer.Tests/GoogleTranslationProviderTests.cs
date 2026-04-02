using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for GoogleTranslationProvider � translation using googletrans.
/// </summary>
public sealed class GoogleTranslationProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _transcriptJsonPath;
    private readonly string _translationJsonPath;
    private readonly string _outputJsonPath;
    private readonly AppLog _log;

    public GoogleTranslationProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"babel-googletrans-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _transcriptJsonPath = Path.Combine(_testDir, "transcript.json");
        _translationJsonPath = Path.Combine(_testDir, "translation.json");
        _outputJsonPath = Path.Combine(_testDir, "output.json");
        _log = new AppLog(Path.Combine(_testDir, "test.log"));
    }

    public void Dispose()
    {
        _log?.Dispose();
        try { Directory.Delete(_testDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ?? TranslateAsync ?????????????????????????????????????????????????????????

    [Fact]
    public async Task TranslateAsync_ThrowsFileNotFoundException_WhenTranscriptNotFound()
    {
        var provider = new GoogleTranslationProvider(_log);
        var request = new TranslationRequest(
            "nonexistent.json",
            _outputJsonPath,
            "es",
            "en",
            "googletrans");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.TranslateAsync(request));
    }

    [Fact]
    public async Task TranslateAsync_ThrowsArgumentException_WhenTranscriptPathNull()
    {
        var provider = new GoogleTranslationProvider(_log);
        var request = new TranslationRequest(
            null!,
            _outputJsonPath,
            "es",
            "en",
            "googletrans");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.TranslateAsync(request));
    }

    [Fact]
    public async Task TranslateAsync_ThrowsArgumentException_WhenOutputPathNull()
    {
        CreateSampleTranscriptJson();
        var provider = new GoogleTranslationProvider(_log);
        var request = new TranslationRequest(
            _transcriptJsonPath,
            null!,
            "es",
            "en",
            "googletrans");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.TranslateAsync(request));
    }

    [Fact]
    public async Task TranslateAsync_SupportsCancellation()
    {
        CreateSampleTranscriptJson();
        var provider = new GoogleTranslationProvider(_log);
        var request = new TranslationRequest(
            _transcriptJsonPath,
            _outputJsonPath,
            "es",
            "en",
            "googletrans");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.TranslateAsync(request, cts.Token));
    }

    // ?? TranslateSingleSegmentAsync ????????????????????????????????????????????

    [Fact]
    public async Task TranslateSingleSegmentAsync_ThrowsArgumentException_WhenSourceTextEmpty()
    {
        CreateSampleTranslationJson();
        var provider = new GoogleTranslationProvider(_log);
        var request = new SingleSegmentTranslationRequest(
            string.Empty,
            "segment_0.0",
            _translationJsonPath,
            _outputJsonPath,
            "es",
            "en",
            "googletrans");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.TranslateSingleSegmentAsync(request));
    }

    [Fact]
    public async Task TranslateSingleSegmentAsync_ThrowsArgumentException_WhenSourceTextWhitespace()
    {
        CreateSampleTranslationJson();
        var provider = new GoogleTranslationProvider(_log);
        var request = new SingleSegmentTranslationRequest(
            "   ",
            "segment_0.0",
            _translationJsonPath,
            _outputJsonPath,
            "es",
            "en",
            "googletrans");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.TranslateSingleSegmentAsync(request));
    }

    [Fact]
    public async Task TranslateSingleSegmentAsync_ThrowsFileNotFoundException_WhenTranslationJsonNotFound()
    {
        var provider = new GoogleTranslationProvider(_log);
        var request = new SingleSegmentTranslationRequest(
            "Hola mundo",
            "segment_0.0",
            "nonexistent.json",
            _outputJsonPath,
            "es",
            "en",
            "googletrans");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.TranslateSingleSegmentAsync(request));
    }

    [Fact]
    public async Task TranslateSingleSegmentAsync_SupportsCancellation()
    {
        CreateSampleTranslationJson();
        var provider = new GoogleTranslationProvider(_log);
        var request = new SingleSegmentTranslationRequest(
            "Hola mundo",
            "segment_0.0",
            _translationJsonPath,
            _outputJsonPath,
            "es",
            "en",
            "googletrans");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.TranslateSingleSegmentAsync(request, cts.Token));
    }

    // ?? Helper methods ?????????????????????????????????????????????????????????

    private void CreateSampleTranscriptJson()
    {
        var json = """
        {
          "language": "es",
          "languageProbability": 0.95,
          "segments": [
            {
              "start": 0.0,
              "end": 2.5,
              "text": "Hola mundo"
            },
            {
              "start": 2.5,
              "end": 5.0,
              "text": "�C�mo est�s?"
            }
          ]
        }
        """;
        File.WriteAllText(_transcriptJsonPath, json);
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
            },
            {
              "id": "segment_2.5",
              "start": 2.5,
              "end": 5.0,
              "text": "�C�mo est�s?",
              "translatedText": "How are you?"
            }
          ]
        }
        """;
        File.WriteAllText(_translationJsonPath, json);
    }
}
