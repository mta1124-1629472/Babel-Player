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

public class OpenAiApiClientPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public OpenAiApiClientPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TranscribeAudioAsync_PerformanceTest()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Create a dummy large file to simulate I/O delay without a large in-memory allocation
            using (var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Write))
            {
                fs.SetLength(1024L * 1024 * 200); // 200 MB sparse file
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