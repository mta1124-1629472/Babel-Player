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

    [Trait("Category", "Integration")]
    [Fact]
    public async Task TranscribeAudioAsync_PerformanceTest()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Keep the payload modest so this opt-in benchmark does not consume excessive memory or I/O.
            byte[] data = new byte[1024 * 1024 * 5]; // 5 MB
            new Random().NextBytes(data);
            await File.WriteAllBytesAsync(tempFile, data);

            using var client = new OpenAiApiClient("test-key", new StubHttpMessageHandler());

            // Warm up
            await client.TranscribeAudioAsync(tempFile, "whisper-1");

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 3; i++)
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