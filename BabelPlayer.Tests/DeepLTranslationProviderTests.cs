using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for DeepLTranslationProvider � translation using DeepL API.
/// </summary>
public sealed class DeepLTranslationProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _transcriptJsonPath;
    private readonly string _translationJsonPath;
    private readonly string _outputJsonPath;
    private readonly AppLog _log;
    private const string TestApiKey = "test-api-key-12345";

    public DeepLTranslationProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"babel-deepl-tests-{Guid.NewGuid():N}");
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

    // ?? Constructor ????????????????????????????????????????????????????????????

    [Fact]
    public void Constructor_AcceptsValidParameters()
    {
        var provider = new DeepLTranslationProvider(_log, TestApiKey);
        Assert.NotNull(provider);
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenApiKeyNull()
    {
        Assert.Throws<ArgumentException>(
            () => new DeepLTranslationProvider(_log, null!));
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenApiKeyEmpty()
    {
        Assert.Throws<ArgumentException>(
            () => new DeepLTranslationProvider(_log, string.Empty));
    }

    // ?? TranslateAsync ?????????????????????????????????????????????????????????

    [Fact]
    public async Task TranslateAsync_ThrowsFileNotFoundException_WhenTranscriptNotFound()
    {
        var provider = new DeepLTranslationProvider(_log, TestApiKey);
        var request = new TranslationRequest(
            "nonexistent.json",
            _outputJsonPath,
            "ES",
            "EN",
            "deepl");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.TranslateAsync(request));
    }

    [Fact]
    public async Task TranslateAsync_ThrowsArgumentException_WhenTranscriptPathNull()
    {
        var provider = new DeepLTranslationProvider(_log, TestApiKey);
        var request = new TranslationRequest(
            null!,
            _outputJsonPath,
            "ES",
            "EN",
            "deepl");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.TranslateAsync(request));
    }

    [Fact]
    public async Task TranslateAsync_ThrowsArgumentException_WhenOutputPathNull()
    {
        CreateSampleTranscriptJson();
        var provider = new DeepLTranslationProvider(_log, TestApiKey);
        var request = new TranslationRequest(
            _transcriptJsonPath,
            null!,
            "ES",
            "EN",
            "deepl");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.TranslateAsync(request));
    }

    [Fact]
    public async Task TranslateAsync_SupportsCancellation()
    {
        CreateSampleTranscriptJson();
        var provider = new DeepLTranslationProvider(_log, TestApiKey);
        var request = new TranslationRequest(
            _transcriptJsonPath,
            _outputJsonPath,
            "ES",
            "EN",
            "deepl");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranslateAsync(request, cts.Token));
    }

    // ?? TranslateSingleSegmentAsync ????????????????????????????????????????????

    [Fact]
    public async Task TranslateSingleSegmentAsync_ThrowsArgumentException_WhenSourceTextEmpty()
    {
        CreateSampleTranslationJson();
        var provider = new DeepLTranslationProvider(_log, TestApiKey);
        var request = new SingleSegmentTranslationRequest(
            string.Empty,
            "segment_0.0",
            _translationJsonPath,
            _outputJsonPath,
            "ES",
            "EN",
            "deepl");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.TranslateSingleSegmentAsync(request));
    }

    [Fact]
    public async Task TranslateSingleSegmentAsync_ThrowsArgumentException_WhenSourceTextWhitespace()
    {
        CreateSampleTranslationJson();
        var provider = new DeepLTranslationProvider(_log, TestApiKey);
        var request = new SingleSegmentTranslationRequest(
            "   ",
            "segment_0.0",
            _translationJsonPath,
            _outputJsonPath,
            "ES",
            "EN",
            "deepl");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.TranslateSingleSegmentAsync(request));
    }

    [Fact]
    public async Task TranslateSingleSegmentAsync_ThrowsFileNotFoundException_WhenTranslationJsonNotFound()
    {
        var provider = new DeepLTranslationProvider(_log, TestApiKey);
        var request = new SingleSegmentTranslationRequest(
            "Hola mundo",
            "segment_0.0",
            "nonexistent.json",
            _outputJsonPath,
            "ES",
            "EN",
            "deepl");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.TranslateSingleSegmentAsync(request));
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
          "sourceLanguage": "ES",
          "targetLanguage": "EN",
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
