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
    public async Task TranscribeAudioAsync_PerformanceTest()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Create a dummy large file to simulate I/O delay
            const int targetSizeMb = 200;
            const int chunkSizeMb = 4;
            const int chunkSizeBytes = chunkSizeMb * 1024 * 1024;
            const int chunks = targetSizeMb / chunkSizeMb;

            var random = new Random();
            var buffer = new byte[chunkSizeBytes];

            await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 8192, useAsync: true))
            {
                for (int i = 0; i < chunks; i++)
                {
                    random.NextBytes(buffer);
                    await fileStream.WriteAsync(buffer, 0, buffer.Length);
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