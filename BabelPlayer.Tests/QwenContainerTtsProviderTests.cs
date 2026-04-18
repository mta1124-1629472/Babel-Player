using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class QwenContainerTtsProviderTests
{
    [Fact]
    public async Task GenerateSegmentTtsAsync_RetriesTransientCanceledRequest_WithFreshReference()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"babel-qwen-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var log = new AppLog(Path.Combine(tempDir, "test.log"));
            var referencePath = Path.Combine(tempDir, "reference.wav");
            var outputPath = Path.Combine(tempDir, "segment.mp3");
            var downloadedAudio = new byte[] { 1, 2, 3, 4, 5 };
            await File.WriteAllBytesAsync(referencePath, new byte[] { 9, 8, 7, 6 });

            var referenceRegistrations = 0;
            var segmentAttempts = 0;

            using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;

                if (path.EndsWith("/tts/qwen/references", StringComparison.Ordinal))
                {
                    referenceRegistrations++;
                    return Task.FromResult(JsonResponse($$"""
                        {"success":true,"reference_id":"ref-{{referenceRegistrations}}"}
                        """));
                }

                if (path.EndsWith("/tts/qwen/segment", StringComparison.Ordinal))
                {
                    segmentAttempts++;
                    if (segmentAttempts == 1)
                        throw new TaskCanceledException("The operation was canceled.");

                    return Task.FromResult(JsonResponse("""
                        {"success":true,"voice":"Qwen/Qwen3-TTS-12Hz-0.6B-Base","audio_path":"C:\\temp\\qwen_retry.wav","file_size_bytes":5}
                        """));
                }

                if (path.EndsWith("/tts/audio/qwen_retry.wav", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(downloadedAudio),
                    });
                }

                throw new InvalidOperationException($"Unexpected request path: {path}");
            }));

            var client = new ContainerizedInferenceClient("http://localhost:8000", log, httpClient, requestLeaseTracker: null);
            await using var provider = new QwenContainerTtsProvider(client, log, new TtsReferenceExtractor(log));

            var result = await provider.GenerateSegmentTtsAsync(
                new SingleSegmentTtsRequest(
                    "Hola mundo",
                    outputPath,
                    "Qwen/Qwen3-TTS-12Hz-0.6B-Base",
                    SpeakerId: "speaker-1",
                    ReferenceAudioPath: referencePath,
                    Language: "en"));

            Assert.True(result.Success);
            Assert.Equal(outputPath, result.AudioPath);
            Assert.True(File.Exists(outputPath));
            Assert.Equal(2, referenceRegistrations);
            Assert.Equal(2, segmentAttempts);
            Assert.Equal(downloadedAudio, await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_DoesNotRetry_WhenCallerCancels()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"babel-qwen-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var log = new AppLog(Path.Combine(tempDir, "test.log"));
            var referencePath = Path.Combine(tempDir, "reference.wav");
            var outputPath = Path.Combine(tempDir, "segment.mp3");
            await File.WriteAllBytesAsync(referencePath, new byte[] { 9, 8, 7, 6 });

            var transportInvocations = 0;

            using var httpClient = new HttpClient(new StubHttpMessageHandler((_, cancellationToken) =>
            {
                transportInvocations++;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(JsonResponse("""
                    {"success":true,"reference_id":"ref-1"}
                    """));
            }));

            var client = new ContainerizedInferenceClient("http://localhost:8000", log, httpClient, requestLeaseTracker: null);
            await using var provider = new QwenContainerTtsProvider(client, log, new TtsReferenceExtractor(log));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateSegmentTtsAsync(
                new SingleSegmentTtsRequest(
                    "Hola mundo",
                    outputPath,
                    "Qwen/Qwen3-TTS-12Hz-0.6B-Base",
                    SpeakerId: "speaker-1",
                    ReferenceAudioPath: referencePath,
                    Language: "en"),
                cts.Token));
            Assert.InRange(transportInvocations, 0, 1);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
