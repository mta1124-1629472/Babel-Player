using System;
using System.Collections.Generic;
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
/// Unit tests for <see cref="ElevenLabsTtsProvider"/>.
/// Covers the Lazy&lt;T&gt; client initialization, IDisposable semantics, CheckReadiness,
/// and validation of TTS requests — all without making real network calls.
/// </summary>
public sealed class ElevenLabsTtsProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly AppLog _log;

    public ElevenLabsTtsProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"babel-elevenlabs-tests-{Guid.NewGuid():N}");
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

    private ElevenLabsApiClient MakeClient() =>
        new("test-key", new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x01, 0x02, 0x03])
            })));

    private string WriteTranslationJson(string? translatedText = "Hello world", bool twoSegments = false)
    {
        var path = Path.Combine(_testDir, $"translation-{Guid.NewGuid():N}.json");

        if (twoSegments)
        {
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
                  "translatedText": "Hello world"
                },
                {
                  "id": "segment_2.5",
                  "start": 2.5,
                  "end": 5.0,
                  "text": "Como estas",
                  "translatedText": "How are you"
                }
              ]
            }
            """;
            File.WriteAllText(path, json);
        }
        else
        {
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
        }

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
        using var provider = new ElevenLabsTtsProvider(_log, "key", MakeClient);
        Assert.Equal(10, provider.MaxConcurrency);
    }

    [Fact]
    public void MaxConcurrency_IsHigherThanInterfaceDefault()
    {
        // ElevenLabs is a cloud provider and should allow more concurrency than local providers.
        using var provider = new ElevenLabsTtsProvider(_log, "key", MakeClient);
        var interfaceDefault = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
        Assert.True(provider.MaxConcurrency > interfaceDefault,
            $"Expected MaxConcurrency={provider.MaxConcurrency} to be greater than interfaceDefault={interfaceDefault}");
        Assert.Equal(10, provider.MaxConcurrency);
    }

    // ── CheckReadiness ─────────────────────────────────────────────────────────

    [Fact]
    public void CheckReadiness_EmptyApiKey_ReturnsNotReady()
    {
        var provider = new ElevenLabsTtsProvider(_log, string.Empty);
        var result = provider.CheckReadiness(MakeSettings());
        Assert.False(result.IsReady);
        Assert.NotNull(result.BlockingReason);
    }

    [Fact]
    public void CheckReadiness_WhitespaceApiKey_ReturnsNotReady()
    {
        var provider = new ElevenLabsTtsProvider(_log, "   ");
        var result = provider.CheckReadiness(MakeSettings());
        Assert.False(result.IsReady);
    }

    [Fact]
    public void CheckReadiness_ValidApiKey_ReturnsReady()
    {
        var provider = new ElevenLabsTtsProvider(_log, "valid-api-key");
        var result = provider.CheckReadiness(MakeSettings());
        Assert.True(result.IsReady);
    }

    // ── Lazy<T> client initialization ─────────────────────────────────────────

    [Fact]
    public void Constructor_DoesNotInvokeClientFactory()
    {
        var callCount = 0;
        _ = new ElevenLabsTtsProvider(_log, "key", () =>
        {
            callCount++;
            return MakeClient();
        });

        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task GenerateTtsAsync_GeneratesCombinedAudio()
    {
        var mockAudioService = new MockAudioProcessingService();
        using var provider = new ElevenLabsTtsProvider(_log, "key", MakeClient, audioProcessingService: mockAudioService);
        var translationPath = WriteTranslationJson(twoSegments: true);
        var outputPath = Path.Combine(_testDir, "out.mp3");

        var result = await provider.GenerateTtsAsync(new TtsRequest(translationPath, outputPath, "eleven_multilingual_v2"));

        Assert.True(result.Success);
        Assert.Equal(outputPath, result.AudioPath);
        Assert.True(File.Exists(outputPath));
        // Verify multi-segment stitching executed (output should be larger than single segment)
        var fileSize = new FileInfo(outputPath).Length;
        Assert.True(fileSize > 0, "Combined audio file should have non-zero size after stitching two segments");
        Assert.True(mockAudioService.CombineAudioSegmentsAsyncCalled, "Audio concatenation should have been called for multi-segment input");
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_InvokesClientFactory_ExactlyOnce()
    {
        var callCount = 0;
        var outputPath = Path.Combine(_testDir, "seg-out.mp3");

        using var provider = new ElevenLabsTtsProvider(_log, "key", () =>
        {
            callCount++;
            return MakeClient();
        });

        try { await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest("Hello", outputPath, "eleven_multilingual_v2")); }
        catch { /* ignore */ }

        try { await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest("World", outputPath, "eleven_multilingual_v2")); }
        catch { /* ignore */ }

        Assert.Equal(1, callCount);
    }

    // ── IDisposable behavior ───────────────────────────────────────────────────

    [Fact]
    public void Dispose_ClientNeverCreated_DoesNotThrow()
    {
        // Dispose should be a no-op when the lazy was never materialized
        var provider = new ElevenLabsTtsProvider(_log, "key", () => MakeClient());
        var ex = Record.Exception(() => provider.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Dispose_ClientWasCreated_DisposesClient()
    {
        var disposed = false;
        var outputPath = Path.Combine(_testDir, "dispose-out.mp3");

        ElevenLabsApiClient CreateTrackedClient()
        {
            // Wrap a real client and intercept the request to avoid real I/O
            var handler = new TrackingDisposeHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x01, 0x02])
                }),
                onDispose: () => disposed = true);
            return new ElevenLabsApiClient("test-key", handler);
        }

        var provider = new ElevenLabsTtsProvider(_log, "key", CreateTrackedClient);

        // Force client creation
        try { await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest("Hi", outputPath, "eleven_multilingual_v2")); }
        catch { /* ignore */ }

        provider.Dispose();

        Assert.True(disposed);
    }

    // ── GenerateTtsAsync validation ────────────────────────────────────────────

    [Fact]
    public async Task GenerateTtsAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        using var provider = new ElevenLabsTtsProvider(_log, "key", MakeClient);
        var request = new TtsRequest("nonexistent.json", Path.Combine(_testDir, "out.mp3"), "eleven_multilingual_v2");

        await Assert.ThrowsAsync<FileNotFoundException>(() => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateTtsAsync_EmptySegments_ThrowsInvalidOperationException()
    {
        using var provider = new ElevenLabsTtsProvider(_log, "key", MakeClient);
        var translationPath = WriteEmptySegmentsTranslationJson();
        var request = new TtsRequest(translationPath, Path.Combine(_testDir, "out.mp3"), "eleven_multilingual_v2");

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateTtsAsync_AllSegmentsNullTranslatedText_ThrowsInvalidOperationException()
    {
        using var provider = new ElevenLabsTtsProvider(_log, "key", MakeClient);
        var translationPath = WriteTranslationJson(translatedText: null);
        var request = new TtsRequest(translationPath, Path.Combine(_testDir, "out.mp3"), "eleven_multilingual_v2");

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateTtsAsync(request));
    }

    // ── Request validation ─────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateSegmentTtsAsync_NullText_ThrowsArgumentException()
    {
        using var provider = new ElevenLabsTtsProvider(_log, "key", MakeClient);
        var request = new SingleSegmentTtsRequest(null!, Path.Combine(_testDir, "out.mp3"), "eleven_multilingual_v2");

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_EmptyText_ThrowsArgumentException()
    {
        using var provider = new ElevenLabsTtsProvider(_log, "key", () => MakeClient());
        var request = new SingleSegmentTtsRequest(string.Empty, Path.Combine(_testDir, "out.mp3"), "eleven_multilingual_v2");

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_WhitespaceText_ThrowsArgumentException()
    {
        using var provider = new ElevenLabsTtsProvider(_log, "key", () => MakeClient());
        var request = new SingleSegmentTtsRequest("   ", Path.Combine(_testDir, "out.mp3"), "eleven_multilingual_v2");

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

    /// <summary>
    /// Manual mock implementation of IAudioProcessingService for testing.
    /// Writes dummy audio data to the output path when CombineAudioSegmentsAsync is called.
    /// </summary>
    private sealed class MockAudioProcessingService : IAudioProcessingService
    {
        public bool CombineAudioSegmentsAsyncCalled { get; private set; }

        public Task CombineAudioSegmentsAsync(
            IReadOnlyList<string> segmentAudioPaths,
            string outputAudioPath,
            CancellationToken cancellationToken)
        {
            CombineAudioSegmentsAsyncCalled = true;
            
            // Create output directory if it doesn't exist
            var outputDir = Path.GetDirectoryName(outputAudioPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);
            
            // Write dummy audio data (3 bytes) to simulate concatenated audio
            File.WriteAllBytes(outputAudioPath, new byte[] { 0x01, 0x02, 0x03 });
            
            return Task.CompletedTask;
        }

        public Task ExtractAudioClipAsync(
            string inputPath,
            string outputPath,
            double startTimeSeconds,
            double durationSeconds,
            CancellationToken cancellationToken)
        {
            // Create output directory if it doesn't exist
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);
            
            // Write dummy audio data
            File.WriteAllBytes(outputPath, new byte[] { 0x01, 0x02, 0x03 });
            
            return Task.CompletedTask;
        }
    }
}