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
/// Unit tests for <see cref="GoogleCloudTtsProvider"/>.
/// Covers the Lazy&lt;T&gt; client initialization, IDisposable semantics, CheckReadiness,
/// and validation of TTS requests — all without making real network calls.
/// </summary>
public sealed class GoogleCloudTtsProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly AppLog _log;

    public GoogleCloudTtsProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"babel-googlecloud-tts-tests-{Guid.NewGuid():N}");
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

    private GoogleApiClient MakeClient() =>
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

    // ── CheckReadiness ─────────────────────────────────────────────────────────

    [Fact]
    public void CheckReadiness_EmptyApiKey_ReturnsNotReady()
    {
        var provider = new GoogleCloudTtsProvider(_log, string.Empty);
        var result = provider.CheckReadiness(MakeSettings());
        Assert.False(result.IsReady);
        Assert.NotNull(result.BlockingReason);
    }

    [Fact]
    public void CheckReadiness_WhitespaceApiKey_ReturnsNotReady()
    {
        var provider = new GoogleCloudTtsProvider(_log, "   ");
        var result = provider.CheckReadiness(MakeSettings());
        Assert.False(result.IsReady);
    }

    [Fact]
    public void CheckReadiness_ValidApiKey_ReturnsReady()
    {
        var provider = new GoogleCloudTtsProvider(_log, "valid-api-key");
        var result = provider.CheckReadiness(MakeSettings());
        Assert.True(result.IsReady);
    }

    // ── Lazy<T> client initialization ─────────────────────────────────────────

    [Fact]
    public void Constructor_DoesNotInvokeClientFactory()
    {
        var callCount = 0;
        _ = new GoogleCloudTtsProvider(_log, "key", () =>
        {
            callCount++;
            return MakeClient();
        });

        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task GenerateTtsAsync_InvokesClientFactory_ExactlyOnce()
    {
        var callCount = 0;
        var translationPath = WriteTranslationJson("Hello world");
        var outputPath = Path.Combine(_testDir, "out.mp3");

        using var provider = new GoogleCloudTtsProvider(_log, "key", () =>
        {
            callCount++;
            return MakeClient();
        });

        // Two calls should still only create the client once (lazy)
        try { await provider.GenerateTtsAsync(new TtsRequest(translationPath, outputPath, "standard")); }
        catch { /* ignore network/content errors */ }

        try { await provider.GenerateTtsAsync(new TtsRequest(translationPath, outputPath, "standard")); }
        catch { /* ignore */ }

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_InvokesClientFactory_ExactlyOnce()
    {
        var callCount = 0;
        var outputPath = Path.Combine(_testDir, "seg-out.mp3");

        using var provider = new GoogleCloudTtsProvider(_log, "key", () =>
        {
            callCount++;
            return MakeClient();
        });

        try { await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest("Hello", outputPath, "standard")); }
        catch { /* ignore */ }

        try { await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest("World", outputPath, "standard")); }
        catch { /* ignore */ }

        Assert.Equal(1, callCount);
    }

    // ── IDisposable behavior ───────────────────────────────────────────────────

    [Fact]
    public void Dispose_ClientNeverCreated_DoesNotThrow()
    {
        var provider = new GoogleCloudTtsProvider(_log, "key", () => MakeClient());
        var ex = Record.Exception(() => provider.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Dispose_ClientWasCreated_DisposesClient()
    {
        var disposed = false;
        var outputPath = Path.Combine(_testDir, "dispose-out.mp3");

        GoogleApiClient CreateTrackedClient()
        {
            var handler = new TrackingDisposeHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x01, 0x02])
                }),
                onDispose: () => disposed = true);
            return new GoogleApiClient("test-key", handler);
        }

        var provider = new GoogleCloudTtsProvider(_log, "key", CreateTrackedClient);

        // Force client creation
        try { await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest("Hi", outputPath, "standard")); }
        catch { /* ignore */ }

        provider.Dispose();

        Assert.True(disposed);
    }

    // ── Request validation ─────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateTtsAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        using var provider = new GoogleCloudTtsProvider(_log, "key", () => MakeClient());
        var request = new TtsRequest("nonexistent.json", Path.Combine(_testDir, "out.mp3"), "standard");

        await Assert.ThrowsAsync<FileNotFoundException>(() => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateTtsAsync_EmptySegments_ThrowsInvalidOperationException()
    {
        using var provider = new GoogleCloudTtsProvider(_log, "key", () => MakeClient());
        var translationPath = WriteEmptySegmentsTranslationJson();
        var request = new TtsRequest(translationPath, Path.Combine(_testDir, "out.mp3"), "standard");

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateTtsAsync_AllSegmentsNullText_ThrowsInvalidOperationException()
    {
        using var provider = new GoogleCloudTtsProvider(_log, "key", () => MakeClient());
        var translationPath = WriteTranslationJson(translatedText: null);
        var request = new TtsRequest(translationPath, Path.Combine(_testDir, "out.mp3"), "standard");

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_EmptyText_ThrowsArgumentException()
    {
        using var provider = new GoogleCloudTtsProvider(_log, "key", () => MakeClient());
        var request = new SingleSegmentTtsRequest(string.Empty, Path.Combine(_testDir, "out.mp3"), "standard");

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_WhitespaceText_ThrowsArgumentException()
    {
        using var provider = new GoogleCloudTtsProvider(_log, "key", () => MakeClient());
        var request = new SingleSegmentTtsRequest("   ", Path.Combine(_testDir, "out.mp3"), "standard");

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateSegmentTtsAsync(request));
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