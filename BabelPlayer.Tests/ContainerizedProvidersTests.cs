using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

public sealed class ContainerizedProvidersTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;

    public ContainerizedProvidersTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-containerized-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "containerized.log"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task ContainerizedTranslationProvider_TranslateAsync_WritesTranslationArtifact()
    {
        var transcriptPath = Path.Combine(_dir, "transcript.json");
        var outputPath = Path.Combine(_dir, "translation.json");

        await File.WriteAllTextAsync(transcriptPath,
            "{\"language\":\"es\",\"language_probability\":1.0,\"segments\":[{\"start\":0.0,\"end\":1.0,\"text\":\"hola\"}]}");

        var client = CreateClient((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/translate")
            {
                return Json(HttpStatusCode.OK,
                    "{\"success\":true,\"source_language\":\"es\",\"target_language\":\"en\",\"segments\":[{\"start\":0.0,\"end\":1.0,\"text\":\"hola\",\"translated_text\":\"hello\"}]}");
            }

            return Json(HttpStatusCode.NotFound, "{\"success\":false,\"error_message\":\"not found\"}");
        });

        var provider = new ContainerizedTranslationProvider(client, _log);

        var result = await provider.TranslateAsync(new TranslationRequest(
            transcriptPath,
            outputPath,
            "es",
            "en",
            "default"));

        Assert.True(result.Success);
        Assert.True(File.Exists(outputPath));

        var artifact = await ArtifactJson.LoadTranslationAsync(outputPath, CancellationToken.None);
        Assert.NotNull(artifact.Segments);
        Assert.Single(artifact.Segments!);
        Assert.Equal("hello", artifact.Segments![0].TranslatedText);
    }

    [Fact]
    public async Task ContainerizedTranslationProvider_TranslateSingleSegmentAsync_UpdatesTargetSegment()
    {
        var translationPath = Path.Combine(_dir, "translation.json");
        var outputPath = Path.Combine(_dir, "translation-updated.json");

        await File.WriteAllTextAsync(translationPath,
            "{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[{\"id\":\"segment_0.0\",\"start\":0.0,\"end\":1.0,\"text\":\"hola\",\"translatedText\":\"hello\"},{\"id\":\"segment_1.0\",\"start\":1.0,\"end\":2.0,\"text\":\"adios\",\"translatedText\":\"bye\"}]}");

        var client = CreateClient((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/translate")
            {
                return Json(HttpStatusCode.OK,
                    "{\"success\":true,\"source_language\":\"es\",\"target_language\":\"en\",\"segments\":[{\"start\":0.0,\"end\":0.0,\"text\":\"hola\",\"translated_text\":\"greetings\"}]}");
            }

            return Json(HttpStatusCode.NotFound, "{\"success\":false,\"error_message\":\"not found\"}");
        });

        var provider = new ContainerizedTranslationProvider(client, _log);

        var result = await provider.TranslateSingleSegmentAsync(new SingleSegmentTranslationRequest(
            "hola",
            "segment_0.0",
            translationPath,
            outputPath,
            "es",
            "en",
            "default"));

        Assert.True(result.Success);

        var artifact = await ArtifactJson.LoadTranslationAsync(outputPath, CancellationToken.None);
        Assert.NotNull(artifact.Segments);
        var first = Assert.Single(artifact.Segments!, s => s.Id == "segment_0.0");
        Assert.Equal("greetings", first.TranslatedText);
        var second = Assert.Single(artifact.Segments!, s => s.Id == "segment_1.0");
        Assert.Equal("bye", second.TranslatedText);
    }

    [Fact]
    public async Task ContainerizedTranslationProvider_TranslateAsync_ThrowsOnSegmentCountMismatch()
    {
        var transcriptPath = Path.Combine(_dir, "transcript.json");
        var outputPath = Path.Combine(_dir, "translation.json");

        await File.WriteAllTextAsync(transcriptPath,
            "{\"language\":\"es\",\"language_probability\":1.0,\"segments\":[{\"start\":0.0,\"end\":1.0,\"text\":\"hola\"}]}");

        var client = CreateClient((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/translate")
            {
                return Json(HttpStatusCode.OK,
                    "{\"success\":true,\"source_language\":\"es\",\"target_language\":\"en\",\"segments\":[{\"start\":0.0,\"end\":1.0,\"text\":\"hola\",\"translated_text\":\"hello\"},{\"start\":1.0,\"end\":2.0,\"text\":\"adios\",\"translated_text\":\"bye\"}]}");
            }

            return Json(HttpStatusCode.NotFound, "{\"success\":false,\"error_message\":\"not found\"}");
        });

        var provider = new ContainerizedTranslationProvider(client, _log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranslateAsync(new TranslationRequest(
            transcriptPath,
            outputPath,
            "es",
            "en",
            "default")));
    }

    [Fact]
    public async Task ContainerizedTtsProvider_GenerateSegmentTtsAsync_DownloadsAudioToRequestedPath()
    {
        var outputPath = Path.Combine(_dir, "segment.mp3");
        var audioBytes = Encoding.UTF8.GetBytes("fake-mp3-bytes");

        var client = CreateClient((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/tts")
            {
                return Json(HttpStatusCode.OK,
                    "{\"success\":true,\"voice\":\"en-US-AriaNeural\",\"audio_path\":\"/tmp/out.mp3\",\"file_size_bytes\":15}");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/tts/audio/out.mp3")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(audioBytes)
                };
                return Task.FromResult(response);
            }

            return Json(HttpStatusCode.NotFound, "{\"success\":false,\"error_message\":\"not found\"}");
        });

        var provider = new ContainerizedTtsProvider(client, _log);

        var result = await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest(
            "hello",
            outputPath,
            "en-US-AriaNeural"));

        Assert.True(result.Success);
        Assert.Equal(outputPath, result.AudioPath);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(audioBytes, await File.ReadAllBytesAsync(outputPath));
    }

    [Fact]
    public async Task ContainerizedTranscriptionProvider_TranscribeAsync_ThrowsWhenInputMissing()
    {
        var client = CreateClient((_, _) =>
            Json(HttpStatusCode.OK, "{\"success\":true,\"language\":\"es\",\"language_probability\":1.0,\"segments\":[]}"));

        var provider = new ContainerizedTranscriptionProvider(client, _log);

        await Assert.ThrowsAsync<FileNotFoundException>(() => provider.TranscribeAsync(new TranscriptionRequest(
            Path.Combine(_dir, "missing.wav"),
            Path.Combine(_dir, "transcript.json"),
            "base")));
    }

    [Fact]
    public async Task ContainerizedTranscriptionProvider_TranscribeAsync_WritesTranscriptArtifact()
    {
        var inputPath = Path.Combine(_dir, "sample.wav");
        var outputPath = Path.Combine(_dir, "transcript.json");
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3, 4]);

        var client = CreateClient((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/transcribe")
            {
                return Json(HttpStatusCode.OK,
                    "{\"success\":true,\"language\":\"es\",\"language_probability\":0.98,\"segments\":[{\"start\":0.0,\"end\":1.2,\"text\":\"hola\"}]}");
            }

            return Json(HttpStatusCode.NotFound, "{\"success\":false,\"error_message\":\"not found\"}");
        });

        var provider = new ContainerizedTranscriptionProvider(client, _log);

        var result = await provider.TranscribeAsync(new TranscriptionRequest(
            inputPath,
            outputPath,
            "base",
            CpuComputeType: "int8",
            CpuThreads: 2,
            NumWorkers: 1));

        Assert.True(result.Success);
        Assert.Equal("es", result.Language);
        Assert.Single(result.Segments);
        Assert.True(File.Exists(outputPath));

        var transcript = await ArtifactJson.LoadTranscriptAsync(outputPath, CancellationToken.None);
        Assert.Equal("es", transcript.Language);
        Assert.Single(transcript.Segments!);
        Assert.Equal("hola", transcript.Segments![0].Text);
    }

    [Fact]
    public async Task ContainerizedTtsProvider_GenerateTtsAsync_CombinesSegmentsAndWritesOutput()
    {
        var translationPath = Path.Combine(_dir, "translation.json");
        var outputPath = Path.Combine(_dir, "combined.mp3");
        var audioBytes = Encoding.UTF8.GetBytes("combined-audio");

        await File.WriteAllTextAsync(translationPath,
            "{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[{\"id\":\"segment_0.0\",\"start\":0.0,\"end\":1.0,\"text\":\"hola\",\"translatedText\":\"hello\"},{\"id\":\"segment_1.0\",\"start\":1.0,\"end\":2.0,\"text\":\"mundo\",\"translatedText\":\"world\"}]}");

        string? postedBody = null;
        var client = CreateClient(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/tts")
            {
                postedBody = await request.Content!.ReadAsStringAsync(ct);
                return await Json(HttpStatusCode.OK,
                    "{\"success\":true,\"voice\":\"en-US-AriaNeural\",\"audio_path\":\"/tmp/combined.mp3\",\"file_size_bytes\":14}");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/tts/audio/combined.mp3")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(audioBytes)
                };
                return response;
            }

            return await Json(HttpStatusCode.NotFound, "{\"success\":false,\"error_message\":\"not found\"}");
        });

        var provider = new ContainerizedTtsProvider(client, _log);

        var result = await provider.GenerateTtsAsync(new TtsRequest(
            translationPath,
            outputPath,
            "en-US-AriaNeural"));

        Assert.True(result.Success);
        Assert.Equal(outputPath, result.AudioPath);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(audioBytes, await File.ReadAllBytesAsync(outputPath));
        Assert.Contains("text=hello+world", postedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainerizedProviderReadiness_CheckTranslationForExecutionAsync_ReturnsReadyWhenCapabilityPresent()
    {
        var probe = new ContainerizedServiceProbe(_log, (_, _, _) => Task.FromResult(new ContainerHealthStatus(
            true,
            false,
            null,
            "http://localhost:8000",
            null,
            new ContainerCapabilitiesSnapshot(
                TranscriptionReady: true,
                TranscriptionDetail: null,
                TranslationReady: true,
                TranslationDetail: null,
                TtsReady: false,
                TtsDetail: "tts disabled"))));

        var settings = new AppSettings { ContainerizedServiceUrl = "http://localhost:8000" };

        var readiness = await ContainerizedProviderReadiness.CheckTranslationForExecutionAsync(settings, probe);

        Assert.True(readiness.IsReady);
        Assert.Null(readiness.BlockingReason);
    }

    [Fact]
    public async Task ContainerizedProviderReadiness_CheckTtsForExecutionAsync_ReturnsNotReadyWhenCapabilityMissing()
    {
        var probe = new ContainerizedServiceProbe(_log, (_, _, _) => Task.FromResult(new ContainerHealthStatus(
            true,
            true,
            "12.6",
            "http://localhost:8000",
            null,
            new ContainerCapabilitiesSnapshot(
                TranscriptionReady: true,
                TranscriptionDetail: null,
                TranslationReady: true,
                TranslationDetail: null,
                TtsReady: false,
                TtsDetail: "model unavailable"))));

        var settings = new AppSettings { ContainerizedServiceUrl = "http://localhost:8000" };

        var readiness = await ContainerizedProviderReadiness.CheckTtsForExecutionAsync(settings, probe);

        Assert.False(readiness.IsReady);
        Assert.Contains("missing TTS capability", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("model unavailable", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerizedProviderReadiness_CheckTranslation_ReturnsCheckingThenUnavailable()
    {
        var probe = new ContainerizedServiceProbe(_log, async (url, _, _) =>
        {
            await Task.Delay(25);
            return ContainerHealthStatus.Unavailable(url, "connection refused");
        });

        var settings = new AppSettings { ContainerizedServiceUrl = "http://localhost:8000" };

        var checking = ContainerizedProviderReadiness.CheckTranslation(settings, probe);
        Assert.False(checking.IsReady);
        Assert.Contains("starting", checking.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        await Task.Delay(100);
        var unavailable = ContainerizedProviderReadiness.CheckTranslation(settings, probe);
        Assert.False(unavailable.IsReady);
        Assert.Contains("Start your local inference service", unavailable.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContainerizedProviderReadiness_CheckTranslation_ReturnsNotReadyWhenCapabilityMissing()
    {
        var probe = new ContainerizedServiceProbe(_log, (url, _, _) =>
            Task.FromResult(new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: false,
                CudaVersion: null,
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(
                    TranscriptionReady: true,
                    TranscriptionDetail: null,
                    TranslationReady: false,
                    TranslationDetail: "warming",
                    TtsReady: true,
                    TtsDetail: null))));

        var settings = new AppSettings { ContainerizedServiceUrl = "http://localhost:8000" };
        _ = ContainerizedProviderReadiness.CheckTranslation(settings, probe);
        Thread.Sleep(75);
        var readiness = ContainerizedProviderReadiness.CheckTranslation(settings, probe);

        Assert.False(readiness.IsReady);
        Assert.Contains("missing translation capability", readiness.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("warming", readiness.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContainerizedProviderReadiness_CheckTts_ReturnsReadyWhenCapabilityPresent()
    {
        var probe = new ContainerizedServiceProbe(_log, (url, _, _) =>
            Task.FromResult(new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: true,
                CudaVersion: "12.6",
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(
                    TranscriptionReady: true,
                    TranscriptionDetail: null,
                    TranslationReady: true,
                    TranslationDetail: null,
                    TtsReady: true,
                    TtsDetail: null))));

        var settings = new AppSettings { ContainerizedServiceUrl = "http://localhost:8000" };
        _ = ContainerizedProviderReadiness.CheckTts(settings, probe);
        Thread.Sleep(75);
        var readiness = ContainerizedProviderReadiness.CheckTts(settings, probe);

        Assert.True(readiness.IsReady);
        Assert.Null(readiness.BlockingReason);
    }

    [Fact]
    public async Task ContainerizedInferenceClient_CheckHealthAsync_ParsesLiveAndCapabilities()
    {
        var client = CreateClient((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/health/live")
            {
                return Json(HttpStatusCode.OK,
                    "{\"status\":\"healthy\",\"cuda_available\":true,\"cuda_version\":\"12.4\"}");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/capabilities")
            {
                return Json(HttpStatusCode.OK,
                    "{\"transcription\":{\"ready\":true,\"detail\":null},\"translation\":{\"ready\":false,\"detail\":\"warming\"},\"tts\":{\"ready\":true,\"detail\":null}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"status\":\"not-found\"}");
        });

        var health = await client.CheckHealthAsync();

        Assert.True(health.IsAvailable);
        Assert.True(health.CudaAvailable);
        Assert.Equal("12.4", health.CudaVersion);
        Assert.NotNull(health.Capabilities);
        Assert.True(health.Capabilities!.TranscriptionReady);
        Assert.False(health.Capabilities.TranslationReady);
        Assert.Equal("warming", health.Capabilities.TranslationDetail);
    }

    private ContainerizedInferenceClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(handler))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        return new ContainerizedInferenceClient("http://localhost:8000", _log, httpClient);
    }

    private static Task<HttpResponseMessage> Json(HttpStatusCode statusCode, string json)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        return Task.FromResult(response);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
