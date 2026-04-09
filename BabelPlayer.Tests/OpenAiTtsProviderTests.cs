using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Babel.Player.Services.Settings;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for <see cref="OpenAiTtsProvider"/>.
/// Covers the Lazy&lt;T&gt; client initialization, IDisposable semantics, CheckReadiness,
/// and validation of TTS requests — all without making real network calls.
/// </summary>
public sealed class OpenAiTtsProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly AppLog _log;

    public OpenAiTtsProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"babel-openai-tts-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _log = new AppLog(Path.Combine(_testDir, "test.log"));
    }

    public void Dispose()
    {
        try { _log.Dispose(); }
        catch { }
        try { Directory.Delete(_testDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static AppSettings MakeSettings() => new();

    private OpenAiApiClient MakeClient() =>
        new("test-key", new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x01, 0x02, 0x03])
            })));

    private string WriteTranslationJson(string? translatedText = "Hello world")
    {
        var path = Path.Combine(_testDir, $"translation-{Guid.NewGuid():N}.json");
        var json = $$"""
        {
          "sourceLanguage": "es",
          "targetLanguage": "en",
          "segments": [
            {
              "id": "segment_0.0",
              "start": 0.0,
              "end": 2.5,
              "text": "Hola mundo",
              "translatedText": {{(translatedText is null ? "null" : $"\"{translatedText}\"")}}
            }
          ]
        }
        """;
        File.WriteAllText(path, json);
        return path;
    }

    private string WriteEmptySegmentsTranslationJson()
    {
        var path = Path.Combine(_testDir, $"empty-translation-{Guid.NewGuid():N}.json");
        var json = """
        {
          "sourceLanguage": "es",
          "targetLanguage": "en",
          "segments": []
        }
        """;
        File.WriteAllText(path, json);
        return path;
    }

    // ── MaxConcurrency ─────────────────────────────────────────────────────────

    [Fact]
    public void MaxConcurrency_ReturnsExpectedValue()
    {
        using var provider = new OpenAiTtsProvider(_log, "key", MakeClient);
        Assert.Equal(12, provider.MaxConcurrency);
    }

    [Fact]
    public void MaxConcurrency_IsHigherThanLocalDefault()
    {
        // OpenAI is a cloud provider and should allow more concurrency than local GPU providers.
        using var provider = new OpenAiTtsProvider(_log, "key", MakeClient);
        Assert.True(provider.MaxConcurrency > 4,
            $"Cloud provider MaxConcurrency={provider.MaxConcurrency} should exceed local GPU limit of 4");
    }

    // ── CheckReadiness ─────────────────────────────────────────────────────────

    [Fact]
    public void CheckReadiness_EmptyApiKey_ReturnsNotReady()
    {
        var provider = new OpenAiTtsProvider(_log, string.Empty);
        var result = provider.CheckReadiness(MakeSettings());
        Assert.False(result.IsReady);
        Assert.NotNull(result.BlockingReason);
    }

    [Fact]
    public void CheckReadiness_WhitespaceApiKey_ReturnsNotReady()
    {
        var provider = new OpenAiTtsProvider(_log, "\t ");
        var result = provider.CheckReadiness(MakeSettings());
        Assert.False(result.IsReady);
    }

    [Fact]
    public void CheckReadiness_ValidApiKey_ReturnsReady()
    {
        var provider = new OpenAiTtsProvider(_log, "sk-test-valid-key");
        var result = provider.CheckReadiness(MakeSettings());
        Assert.True(result.IsReady);
    }

    // ── GenerateTtsAsync behavior ──────────────────────────────────────────────

    [Fact]
    public async Task GenerateTtsAsync_ThrowsNotImplementedException()
    {
        using var provider = new OpenAiTtsProvider(_log, "key", MakeClient);
        var translationPath = WriteTranslationJson("Hello world");
        var outputPath = Path.Combine(_testDir, "out.mp3");

        var ex = await Assert.ThrowsAsync<NotImplementedException>(() =>
            provider.GenerateTtsAsync(new TtsRequest(translationPath, outputPath, "tts-1")));
        Assert.Contains("PLACEHOLDER", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_InvokesClientFactory_ExactlyOnce()
    {
        var callCount = 0;
        var outputPath = Path.Combine(_testDir, "seg-out.mp3");

        using var provider = new OpenAiTtsProvider(_log, "key", () =>
        {
            callCount++;
            return MakeClient();
        });

        try { await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest("Hello", outputPath, "tts-1")); }
        catch { /* ignore */ }

        try { await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest("World", outputPath, "tts-1")); }
        catch { /* ignore */ }

        Assert.Equal(1, callCount);
    }

    // ── IDisposable behavior ───────────────────────────────────────────────────

    [Fact]
    public void Dispose_ClientNeverCreated_DoesNotThrow()
    {
        var provider = new OpenAiTtsProvider(_log, "key", () => MakeClient());
        var ex = Record.Exception(() => provider.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Dispose_ClientWasCreated_DisposesClient()
    {
        var disposed = false;
        var outputPath = Path.Combine(_testDir, "dispose-out.mp3");

        OpenAiApiClient CreateTrackedClient()
        {
            var handler = new TrackingDisposeHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x01, 0x02])
                }),
                onDispose: () => disposed = true);
            return new OpenAiApiClient("test-key", handler);
        }

        var provider = new OpenAiTtsProvider(_log, "key", CreateTrackedClient);

        // Force client creation
        try { await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest("Hi", outputPath, "tts-1")); }
        catch { /* ignore */ }

        provider.Dispose();

        Assert.True(disposed);
    }

    // ── Request validation ─────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateSegmentTtsAsync_NullText_ThrowsArgumentException()
    {
        using var provider = new OpenAiTtsProvider(_log, "key", MakeClient);
        var request = new SingleSegmentTtsRequest(null!, Path.Combine(_testDir, "out.mp3"), "tts-1");

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_EmptyText_ThrowsArgumentException()
    {
        using var provider = new OpenAiTtsProvider(_log, "key", () => MakeClient());
        var request = new SingleSegmentTtsRequest(string.Empty, Path.Combine(_testDir, "out.mp3"), "tts-1");

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_WhitespaceText_ThrowsArgumentException()
    {
        using var provider = new OpenAiTtsProvider(_log, "key", () => MakeClient());
        var request = new SingleSegmentTtsRequest("  ", Path.Combine(_testDir, "out.mp3"), "tts-1");

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateSegmentTtsAsync(request));
    }

    // ── Model name normalization ───────────────────────────────────────────────

    [Fact]
    public async Task GenerateSegmentTtsAsync_UnrecognizedVoiceName_FallsBackToTts1()
    {
        // Even with an unrecognized voice name, the method should proceed (normalization defaults to tts-1).
        var callCount = 0;
        var outputPath = Path.Combine(_testDir, "model-out.mp3");

        using var provider = new OpenAiTtsProvider(_log, "key", () =>
        {
            callCount++;
            return MakeClient();
        });

        await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest("Hello", outputPath, "unknown-model"));

        Assert.Equal(1, callCount);
        Assert.True(File.Exists(outputPath));
    }

    // ── Stub helpers ──────────────────────────────────────────────────────────

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }

    private sealed class TrackingDisposeHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        Action onDispose) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                onDispose();
            base.Dispose(disposing);
        }
    }
}