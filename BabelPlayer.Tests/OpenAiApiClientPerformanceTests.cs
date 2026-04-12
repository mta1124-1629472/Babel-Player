using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;
using Xunit.Abstractions;

namespace BabelPlayer.Tests;

public class OpenAiApiClientPerformanceTests : IClassFixture<SessionWorkflowTemplateFixture>
{
    private readonly ITestOutputHelper _output;
    private readonly SessionWorkflowTemplateFixture _fixture;

    public OpenAiApiClientPerformanceTests(ITestOutputHelper output, SessionWorkflowTemplateFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    private class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                // This simulates the actual reading of the stream content
                await request.Content.CopyToAsync(Stream.Null, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ \"language\": \"en\", \"text\": \"test\", \"segments\": [] }")
            };
        }
    }

    [Fact(Skip = "Benchmark - run in dedicated workflow")]
    [Trait("Category", "Benchmark")]
    public async Task TranscribeAudioAsync_PerformanceTest()
    {
        var tempDir = _fixture.CreateCaseDirectory(nameof(TranscribeAudioAsync_PerformanceTest));
        var tempFile = Path.Combine(tempDir, "perf_dummy_audio.tmp");
        try
        {
            // Create a dummy large file to simulate I/O delay without allocating the full payload in memory
            const int totalSizeBytes = 1024 * 1024 * 200; // 200 MB
            const int bufferSizeBytes = 1024 * 1024; // 1 MB
            byte[] buffer = new byte[bufferSizeBytes];
            var random = Random.Shared;

            await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSizeBytes, useAsync: true))
            {
                int remaining = totalSizeBytes;
                while (remaining > 0)
                {
                    int bytesToWrite = Math.Min(buffer.Length, remaining);
                    random.NextBytes(buffer);
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesToWrite));
                    remaining -= bytesToWrite;
                }
            }

            using var client = new OpenAiApiClient("test-key", new StubHttpMessageHandler());

            // Warm up
            await client.TranscribeAudioAsync(tempFile, "whisper-1");

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++)
            {
                await client.TranscribeAudioAsync(tempFile, "whisper-1");
            }
            sw.Stop();

            _output.WriteLine($"TranscribeAudioAsync time: {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
