using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

public sealed class ContainerizedProvidersTests() : IDisposable
{
    private readonly TestContext _ctx = new();

    private sealed class TestContext
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"babel-containerized-tests-{Guid.NewGuid():N}");
        public AppLog Log { get; }

        public TestContext()
        {
            Directory.CreateDirectory(Dir);
            Log = new AppLog(Path.Combine(Dir, "containerized.log"));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_ctx.Dir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task ContainerizedTranslationProvider_TranslateAsync_WritesTranslationArtifact()
    {
        var transcriptPath = Path.Combine(_ctx.Dir, "transcript.json");
        var outputPath = Path.Combine(_ctx.Dir, "translation.json");

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

        var provider = new ContainerizedTranslationProvider(client, _ctx.Log, "nllb-200-distilled-1.3B");

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
        var translationPath = Path.Combine(_ctx.Dir, "translation.json");
        var outputPath = Path.Combine(_ctx.Dir, "translation-updated.json");

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

        var provider = new ContainerizedTranslationProvider(client, _ctx.Log, "nllb-200-distilled-1.3B");

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
        var transcriptPath = Path.Combine(_ctx.Dir, "transcript.json");
        var outputPath = Path.Combine(_ctx.Dir, "translation.json");

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

        var provider = new ContainerizedTranslationProvider(client, _ctx.Log, "nllb-200-distilled-1.3B");

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
        var outputPath = Path.Combine(_ctx.Dir, "segment.mp3");
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

        var provider = new ContainerizedTtsProvider(client, _ctx.Log);

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

        var provider = new ContainerizedTranscriptionProvider(client, _ctx.Log);

        await Assert.ThrowsAsync<FileNotFoundException>(() => provider.TranscribeAsync(new TranscriptionRequest(
            Path.Combine(_ctx.Dir, "missing.wav"),
            Path.Combine(_ctx.Dir, "transcript.json"),
            "base")));
    }

    [Fact]
    public async Task ContainerizedTranscriptionProvider_TranscribeAsync_WritesTranscriptArtifact()
    {
        var inputPath = Path.Combine(_ctx.Dir, "sample.wav");
        var outputPath = Path.Combine(_ctx.Dir, "transcript.json");
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

        var provider = new ContainerizedTranscriptionProvider(client, _ctx.Log);

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

    // ── QwenContainerTtsProvider ───────────────────────────────────────────────

    [Fact]
    public async Task QwenContainerTtsProvider_GenerateSegmentTtsAsync_PostsToQwenEndpointWithReferenceAudio()
    {
        var outputPath = Path.Combine(_ctx.Dir, "qwen-segment.mp3");
        var referenceAudioPath = Path.Combine(_ctx.Dir, "speaker-a.wav");
        await File.WriteAllBytesAsync(referenceAudioPath, Encoding.UTF8.GetBytes("ref-audio"));
        var audioBytes = Encoding.UTF8.GetBytes("qwen-segment-bytes");
        string? postedModel = null;

        var client = CreateClient(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/tts/qwen/references")
            {
                return await Json(HttpStatusCode.OK,
                    "{\"success\":true,\"reference_id\":\"spk_default_stub123\"}");
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/tts/qwen/segment")
            {
                postedModel = request.RequestUri.AbsolutePath;
                var body = await request.Content!.ReadAsStringAsync(ct);
                return await Json(HttpStatusCode.OK,
                    "{\"success\":true,\"voice\":\"Qwen/Qwen3-TTS-12Hz-1.7B-Base\",\"audio_path\":\"/tmp/qwen-segment.mp3\",\"file_size_bytes\":18}");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/tts/audio/qwen-segment.mp3")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(audioBytes)
                };
            }

            return await Json(HttpStatusCode.NotFound, "{\"success\":false,\"error_message\":\"not found\"}");
        });

        var provider = new QwenContainerTtsProvider(client, _ctx.Log, new TtsReferenceExtractor(_ctx.Log));

        var result = await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest(
            "hello there",
            outputPath,
            "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
            SpeakerId: null,
            ReferenceAudioPath: referenceAudioPath,
            Language: "en"));

        Assert.True(result.Success);
        Assert.Equal(outputPath, result.AudioPath);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(audioBytes, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal("/tts/qwen/segment", postedModel);
    }

    [Fact]
    public async Task QwenContainerTtsProvider_GenerateSegmentTtsAsync_ThrowsWhenNoReferenceAudio()
    {
        var outputPath = Path.Combine(_ctx.Dir, "qwen-missing-ref.mp3");
        var client = CreateClient((_, _) => Json(HttpStatusCode.OK, "{}"));
        var provider = new QwenContainerTtsProvider(client, _ctx.Log, new TtsReferenceExtractor(_ctx.Log));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest(
                "hello",
                outputPath,
                "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
                SpeakerId: null,
                ReferenceAudioPath: null,
                Language: "en")));
    }

    [Fact]
    public async Task QwenContainerTtsProvider_GenerateSegmentTtsAsync_DeduplicatesReferenceRegistrationPerSpeaker()
    {
        // Three calls — two for spk_00, one for spk_01 — only 2 registrations should occur.
        var spk00Ref = Path.Combine(_ctx.Dir, "spk00.wav");
        var spk01Ref = Path.Combine(_ctx.Dir, "spk01.wav");

        await File.WriteAllBytesAsync(spk00Ref, Encoding.UTF8.GetBytes("ref-spk00"));
        await File.WriteAllBytesAsync(spk01Ref, Encoding.UTF8.GetBytes("ref-spk01"));

        var registrationCount = 0;
        var segmentCount = 0;
        var audioBytes = Encoding.UTF8.GetBytes("x");

        var client = CreateClient(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/tts/qwen/references")
            {
                registrationCount++;
                var body = await request.Content!.ReadAsStringAsync(ct);
                var refId = body.Contains("spk_00", StringComparison.Ordinal) ? "reg-spk00" : "reg-spk01";
                return await Json(HttpStatusCode.OK, $"{{\"success\":true,\"reference_id\":\"{refId}\"}}");
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/tts/qwen/segment")
            {
                segmentCount++;
                return await Json(HttpStatusCode.OK,
                    $"{{\"success\":true,\"voice\":\"Qwen/Qwen3-TTS-12Hz-1.7B-Base\",\"audio_path\":\"/tmp/q{segmentCount}.mp3\",\"file_size_bytes\":1}}");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.StartsWith("/tts/audio/", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audioBytes) };
            }

            return await Json(HttpStatusCode.NotFound, "{\"success\":false,\"error_message\":\"not found\"}");
        });

        var provider = new QwenContainerTtsProvider(client, _ctx.Log, new TtsReferenceExtractor(_ctx.Log));

        // spk_00 first call
        var r1 = await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest(
            "hello", Path.Combine(_ctx.Dir, "seg1.mp3"), "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
            SpeakerId: "spk_00", ReferenceAudioPath: spk00Ref, Language: "en"));
        Assert.True(r1.Success);

        // spk_01 call
        var r2 = await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest(
            "world", Path.Combine(_ctx.Dir, "seg2.mp3"), "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
            SpeakerId: "spk_01", ReferenceAudioPath: spk01Ref, Language: "en"));
        Assert.True(r2.Success);

        // spk_00 second call — should reuse cached reference
        var r3 = await provider.GenerateSegmentTtsAsync(new SingleSegmentTtsRequest(
            "again", Path.Combine(_ctx.Dir, "seg3.mp3"), "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
            SpeakerId: "spk_00", ReferenceAudioPath: spk00Ref, Language: "en"));
        Assert.True(r3.Success);

        Assert.Equal(2, registrationCount);  // one registration per speaker, not per segment
        Assert.Equal(3, segmentCount);       // three synthesis calls total
    }

    [Fact]
    public async Task QwenContainerTtsProvider_GenerateTtsAsync_ThrowsNotImplementedException()
    {
        // Combined generation is now delegated to the coordinator; provider must throw.
        var client = CreateClient((_, _) =>
            Json(HttpStatusCode.OK, "{\"success\":true}"));
        var provider = new QwenContainerTtsProvider(client, _ctx.Log, new TtsReferenceExtractor(_ctx.Log));
        var request = new TtsRequest(
            Path.Combine(_ctx.Dir, "dummy.json"),
            Path.Combine(_ctx.Dir, "out.mp3"),
            "Qwen/Qwen3-TTS-12Hz-1.7B-Base");

        var ex = await Assert.ThrowsAsync<NotImplementedException>(
            () => provider.GenerateTtsAsync(request));

        Assert.Contains("PLACEHOLDER", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QwenContainerTtsProvider_Constructor_AcceptsSimplifiedParameters()
    {
        // Verify the constructor no longer requires a combineAudioFunc parameter.
        var client = CreateClient((_, _) =>
            Json(HttpStatusCode.OK, "{\"success\":true}"));

        var ex = Record.Exception(
            () => new QwenContainerTtsProvider(client, _ctx.Log, new TtsReferenceExtractor(_ctx.Log)));

        Assert.Null(ex);
    }

    [Fact]
    public void QwenContainerTtsProvider_MaxConcurrency_UsesRuntimePolicy()
    {
        Environment.SetEnvironmentVariable(QwenRuntimePolicy.MaxConcurrencyEnvironmentVariable, "2");
        try
        {
            var client = CreateClient((_, _) => Json(HttpStatusCode.OK, "{\"success\":true}"));
            var provider = new QwenContainerTtsProvider(client, _ctx.Log, new TtsReferenceExtractor(_ctx.Log));

            Assert.Equal(2, provider.MaxConcurrency);
        }
        finally
        {
            Environment.SetEnvironmentVariable(QwenRuntimePolicy.MaxConcurrencyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task ContainerizedTtsProvider_GenerateTtsAsync_CombinesSegmentsAndWritesOutput()
    {
        var translationPath = Path.Combine(_ctx.Dir, "combined-translation.json");
        var outputPath = Path.Combine(_ctx.Dir, "combined-output.mp3");
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

        var provider = new ContainerizedTtsProvider(client, _ctx.Log);

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
        var probe = new ContainerizedServiceProbe(_ctx.Log, (_, _, _) => Task.FromResult(new ContainerHealthStatus(
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

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.DockerHost,
            ContainerizedServiceUrl = "http://localhost:8000"
        };

        var readiness = await ContainerizedProviderReadiness.CheckTranslationForExecutionAsync(settings, probe);

        Assert.True(readiness.IsReady);
        Assert.Null(readiness.BlockingReason);
    }

    [Fact]
    public async Task ContainerizedProviderReadiness_CheckTtsForExecutionAsync_ReturnsNotReadyWhenCapabilityMissing()
    {
        var probe = new ContainerizedServiceProbe(_ctx.Log, (_, _, _) => Task.FromResult(new ContainerHealthStatus(
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
                TtsDetail: "model unavailable",
                TtsProviders: new Dictionary<string, bool>
                {
                    [ProviderNames.Qwen] = false,
                },
                TtsProviderDetails: new Dictionary<string, string>
                {
                    [ProviderNames.Qwen] = "model unavailable",
                }))));

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.DockerHost,
            ContainerizedServiceUrl = "http://localhost:8000",
            TtsProvider = ProviderNames.Qwen,
        };

        var readiness = await ContainerizedProviderReadiness.CheckTtsForExecutionAsync(settings, probe);

        Assert.False(readiness.IsReady);
        Assert.Contains("missing TTS capability", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("model unavailable", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerizedProviderReadiness_CheckTranslation_ReturnsCheckingThenUnavailable()
    {
        var probe = new ContainerizedServiceProbe(_ctx.Log, async (url, _, ct) =>
        {
            await Task.Delay(25, ct);
            return ContainerHealthStatus.Unavailable(url, "connection refused");
        });

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.DockerHost,
            ContainerizedServiceUrl = "http://localhost:8000"
        };

        var checking = ContainerizedProviderReadiness.CheckTranslation(settings, probe);
        Assert.False(checking.IsReady);
        Assert.Contains("starting", checking.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var unavailable = checking;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(50);
            unavailable = ContainerizedProviderReadiness.CheckTranslation(settings, probe);
            if (!string.IsNullOrWhiteSpace(unavailable.BlockingReason) &&
                !unavailable.BlockingReason.Contains("starting", StringComparison.OrdinalIgnoreCase))
                break;
        }

        Assert.False(unavailable.IsReady);
        Assert.Contains("Start your local Docker GPU host", unavailable.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContainerizedProviderReadiness_CheckTranslation_ReturnsNotReadyWhenCapabilityMissing()
    {
        var probe = new ContainerizedServiceProbe(_ctx.Log, (url, _, _) =>
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

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.DockerHost,
            ContainerizedServiceUrl = "http://localhost:8000"
        };
        _ = ContainerizedProviderReadiness.CheckTranslation(settings, probe);
        ProviderReadiness readiness;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        do
        {
            readiness = ContainerizedProviderReadiness.CheckTranslation(settings, probe);
            if (readiness.BlockingReason?.Contains("is starting at", StringComparison.OrdinalIgnoreCase) != true)
                break;
            Thread.Sleep(10);
        } while (DateTimeOffset.UtcNow < deadline);

        Assert.False(readiness.IsReady);
        Assert.Contains("missing translation capability", readiness.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("warming", readiness.BlockingReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContainerizedProviderReadiness_CheckTts_ReturnsReadyWhenCapabilityPresent()
    {
        var probe = new ContainerizedServiceProbe(_ctx.Log, (url, _, _) =>
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
                TtsDetail: null,
                TtsProviders: new Dictionary<string, bool>
                {
                    [ProviderNames.Qwen] = true,
                },
                TtsProviderDetails: new Dictionary<string, string>
                {
                    [ProviderNames.Qwen] = "Qwen3-TTS ready",
                }))));

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.DockerHost,
            ContainerizedServiceUrl = "http://localhost:8000",
            TtsProvider = ProviderNames.Qwen,
        };
        _ = ContainerizedProviderReadiness.CheckTts(settings, probe);
        ProviderReadiness readiness;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        do
        {
            readiness = ContainerizedProviderReadiness.CheckTts(settings, probe);
            if (readiness.IsReady) break;
            Thread.Sleep(10);
        } while (DateTimeOffset.UtcNow < deadline);

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

    [Fact]
    public async Task ContainerizedInferenceClient_CheckHealthAsync_ParsesProviderSpecificTtsCapabilities()
    {
        var client = CreateClient((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/health/live")
            {
                return Json(HttpStatusCode.OK,
                    "{\"status\":\"healthy\",\"cuda_available\":true,\"cuda_version\":\"12.8\"}");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/capabilities")
            {
                return Json(HttpStatusCode.OK,
                    "{\"transcription\":{\"ready\":true},\"translation\":{\"ready\":true},\"tts\":{\"ready\":true,\"detail\":\"XTTS v2 ready on cuda; reference audio required\",\"providers\":{\"qwen-tts\":false,\"xtts-container\":true},\"provider_details\":{\"qwen-tts\":\"Qwen3-TTS warmup failed: paging file too small\",\"xtts-container\":\"XTTS v2 ready on cuda; reference audio required\"}}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"status\":\"not-found\"}");
        });

        var health = await client.CheckHealthAsync();

        Assert.NotNull(health.Capabilities);
        Assert.True(health.Capabilities!.TryGetTtsProviderReadiness(ProviderNames.Qwen, out var qwenReady, out var qwenDetail));
        Assert.False(qwenReady);
        Assert.Contains("paging file", qwenDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerizedInferenceClient_CheckHealthAsync_ParsesQwenMetricsAndProviderHealth()
    {
        var client = CreateClient((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/health/live")
            {
                return Json(HttpStatusCode.OK,
                    "{\"status\":\"healthy\",\"cuda_available\":true,\"cuda_version\":\"12.8\",\"active_requests\":2,\"active_qwen_requests\":1,\"busy\":true,\"busy_reason\":\"qwen queued (1); max concurrency 2\",\"qwen_max_concurrency\":2,\"qwen_queue_depth\":1,\"qwen_last_queue_wait_ms\":321.5,\"qwen_last_generation_ms\":24567.0,\"qwen_last_reference_prep_ms\":812.0,\"qwen_last_warmup_ms\":6789.0,\"provider_health\":{\"qwen-tts\":{\"ready\":false,\"state\":\"warming\",\"detail\":\"Qwen3-TTS warming up\",\"checked_at\":\"2026-04-10T23:50:34Z\",\"is_stale\":false,\"failure_category\":null,\"metrics\":{\"queue_depth\":1},\"history\":[{\"timestamp\":\"2026-04-10T23:49:00Z\",\"state\":\"warming\",\"ready\":false,\"detail\":\"Qwen3-TTS warming up\"}]}}}");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/capabilities")
            {
                return Json(HttpStatusCode.OK,
                    "{\"transcription\":{\"ready\":true},\"translation\":{\"ready\":true},\"tts\":{\"ready\":false,\"detail\":\"qwen pending\",\"providers\":{\"qwen-tts\":false},\"provider_details\":{\"qwen-tts\":\"Qwen3-TTS warming up\"},\"provider_health\":{\"qwen-tts\":{\"ready\":false,\"state\":\"warming\",\"detail\":\"Qwen3-TTS warming up\",\"checked_at\":\"2026-04-10T23:50:34Z\",\"is_stale\":false,\"failure_category\":null,\"metrics\":{\"queue_depth\":1},\"history\":[{\"timestamp\":\"2026-04-10T23:49:00Z\",\"state\":\"warming\",\"ready\":false,\"detail\":\"Qwen3-TTS warming up\"}]}}}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"status\":\"not-found\"}");
        });

        var health = await client.CheckHealthAsync();

        Assert.Equal(2, health.QwenMaxConcurrency);
        Assert.Equal(1, health.QwenQueueDepth);
        Assert.Equal(321.5, health.QwenLastQueueWaitMs);
        Assert.Equal(24567.0, health.QwenLastGenerationMs);
        Assert.Equal(812.0, health.QwenLastReferencePrepMs);
        Assert.Equal(6789.0, health.QwenLastWarmupMs);
        Assert.NotNull(health.ProviderHealth);
        Assert.True(health.ProviderHealth!.TryGetValue(ProviderNames.Qwen, out var liveProviderHealth));
        Assert.Equal("warming", liveProviderHealth!.State);
        Assert.NotNull(health.Capabilities);
        Assert.True(health.Capabilities!.TryGetTtsProviderHealth(ProviderNames.Qwen, out var capabilityProviderHealth));
        Assert.Equal("Qwen3-TTS warming up", capabilityProviderHealth!.Detail);
        Assert.Single(capabilityProviderHealth.History);
        Assert.Contains("qwen-queue=1", health.StatusLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("qwen-max=2", health.StatusLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerizedInferenceClient_CheckHealthAsync_ParsesDiarizationCapabilities()
    {
        var client = CreateClient((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/health/live")
            {
                return Json(HttpStatusCode.OK,
                    "{\"status\":\"healthy\",\"cuda_available\":true,\"cuda_version\":\"12.8\"}");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/capabilities")
            {
                return Json(HttpStatusCode.OK,
                    "{\"transcription\":{\"ready\":true},\"translation\":{\"ready\":true},\"tts\":{\"ready\":true},\"diarization\":{\"ready\":true,\"detail\":\"Diarization available\",\"providers\":{\"nemo\":true},\"provider_details\":{\"nemo\":\"NeMo ready\"},\"default_provider\":\"nemo\"}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"status\":\"not-found\"}");
        });

        var health = await client.CheckHealthAsync();

        Assert.NotNull(health.Capabilities);
        Assert.True(health.Capabilities!.DiarizationReady);
        Assert.Equal(ProviderNames.NemoLocal, health.Capabilities.DiarizationDefaultProvider);
        Assert.True(health.Capabilities.TryGetDiarizationProviderReadiness(ProviderNames.NemoLocal, out var nemoReady, out var nemoDetail));
        Assert.True(nemoReady);
        Assert.Equal("NeMo ready", nemoDetail);
        Assert.False(health.Capabilities.TryGetDiarizationProviderReadiness(ProviderNames.WeSpeakerLocal, out var wespeakerReady, out var wespeakerDetail));
        Assert.False(wespeakerReady);
        Assert.Null(wespeakerDetail);
    }

    [Fact]
    public async Task ContainerizedInferenceClient_CheckHealthAsync_ParsesContractInvalidNemoDiarizationCapabilityDetail()
    {
        var client = CreateClient((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/health/live")
            {
                return Json(HttpStatusCode.OK,
                    "{\"status\":\"healthy\",\"cuda_available\":true,\"cuda_version\":\"12.8\"}");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/capabilities")
            {
                return Json(HttpStatusCode.OK,
                    "{\"transcription\":{\"ready\":true},\"translation\":{\"ready\":true},\"tts\":{\"ready\":true},\"diarization\":{\"ready\":false,\"detail\":\"nemo: NeMo diarization config contract invalid: Key 'device' is not in struct\",\"providers\":{\"nemo\":false},\"provider_details\":{\"nemo\":\"NeMo diarization config contract invalid: Key 'device' is not in struct\"},\"default_provider\":\"nemo\"}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"status\":\"not-found\"}");
        });

        var health = await client.CheckHealthAsync();

        Assert.NotNull(health.Capabilities);
        Assert.False(health.Capabilities!.DiarizationReady);
        Assert.True(health.Capabilities.TryGetDiarizationProviderReadiness(ProviderNames.NemoLocal, out var nemoReady, out var nemoDetail));
        Assert.False(nemoReady);
        Assert.Contains("contract invalid", nemoDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("device", nemoDetail, StringComparison.OrdinalIgnoreCase);
        Assert.False(health.Capabilities.TryGetDiarizationProviderReadiness(ProviderNames.WeSpeakerLocal, out _, out var wespeakerDetail));
        Assert.Null(wespeakerDetail);
    }

    [Fact]
    public async Task ContainerizedInferenceClient_DiarizeAsync_UsesNemoEndpointAndNormalizesSpeakerIds()
    {
        var inputPath = Path.Combine(_ctx.Dir, "diarize.wav");
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3, 4]);
        string? postedPath = null;

        var client = CreateClient((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postedPath = request.RequestUri?.AbsolutePath;
                return Json(HttpStatusCode.OK,
                    "{\"success\":true,\"speaker_count\":2,\"segments\":[{\"start\":0.0,\"end\":1.0,\"speaker_id\":\"speaker_0\"},{\"start\":1.0,\"end\":2.0,\"speaker_id\":\"speaker_1\"}]}");
            }

            return Json(HttpStatusCode.NotFound, "{\"success\":false,\"error_message\":\"not found\"}");
        });

        var result = await client.DiarizeAsync(inputPath, "nemo", minSpeakers: 1, maxSpeakers: 2);

        Assert.True(result.Success);
        Assert.Equal("/diarize", postedPath);
        Assert.Equal(2, result.SpeakerCount);
        Assert.Collection(result.Segments,
            first => Assert.Equal("spk_00", first.SpeakerId),
            second => Assert.Equal("spk_01", second.SpeakerId));
    }

    [Fact]
    public async Task ContainerizedInferenceClient_DiarizeAsync_RejectsWeSpeakerOnContainerizedClient()
    {
        var inputPath = Path.Combine(_ctx.Dir, "wespeaker.wav");
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3, 4]);
        var requestCount = 0;

        var client = CreateClient((request, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Json(HttpStatusCode.OK,
                "{\"success\":true,\"speaker_count\":2,\"segments\":[{\"start\":0.0,\"end\":1.0,\"speaker_id\":\"1\"},{\"start\":1.0,\"end\":2.0,\"speaker_id\":\"2\"}]}");
        });

        var result = await client.DiarizeAsync(inputPath, ProviderNames.WeSpeakerLocal);

        Assert.False(result.Success);
        Assert.Equal(0, requestCount);
        Assert.Contains("managed CPU runtime", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Segments);
        Assert.Equal(0, result.SpeakerCount);
    }

    [Fact]
    public async Task ContainerizedInferenceClient_CheckHealthAsync_ReturnsLiveButWarmingWhenCapabilitiesProbeFails()
    {
        var client = CreateClient((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/health/live")
            {
                return Json(HttpStatusCode.OK,
                    "{\"status\":\"healthy\",\"cuda_available\":true,\"cuda_version\":\"12.8\",\"active_requests\":4,\"active_qwen_requests\":2,\"active_diarization_requests\":1,\"busy\":true,\"busy_reason\":\"Qwen warmup in progress\"}");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/capabilities")
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 5 seconds elapsing.");

            return Json(HttpStatusCode.NotFound, "{\"status\":\"not-found\"}");
        });

        var health = await client.CheckHealthAsync();

        Assert.True(health.IsAvailable);
        Assert.True(health.CudaAvailable);
        Assert.Equal(4, health.ActiveRequests);
        Assert.Equal(2, health.ActiveQwenRequests);
        Assert.Equal(1, health.ActiveDiarizationRequests);
        Assert.True(health.Busy);
        Assert.Equal("Qwen warmup in progress", health.BusyReason);
        Assert.Null(health.ErrorMessage);
        Assert.Null(health.Capabilities);
        Assert.NotNull(health.CapabilitiesError);
        Assert.Contains("HttpClient.Timeout", health.CapabilitiesError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("active=4", health.StatusLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("qwen=2", health.StatusLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("diarization=1", health.StatusLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("busy=Qwen warmup in progress", health.StatusLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("capabilities unavailable", health.StatusLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerizedProviderReadiness_CheckTranslationForExecutionAsync_ReturnsLiveButWarmingMessage()
    {
        var warmingDetail = "Capabilities probe is still warming or failed: timeout";
        var probe = new ContainerizedServiceProbe(_ctx.Log, (_, _, _) => Task.FromResult(new ContainerHealthStatus(
            true,
            true,
            "12.8",
            "http://localhost:8000",
            warmingDetail,
            new ContainerCapabilitiesSnapshot(
                TranscriptionReady: false,
                TranscriptionDetail: warmingDetail,
                TranslationReady: false,
                TranslationDetail: warmingDetail,
                TtsReady: false,
                TtsDetail: warmingDetail))));

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv,
            ContainerizedServiceUrl = "http://localhost:8000"
        };

        var readiness = await ContainerizedProviderReadiness.CheckTranslationForExecutionAsync(settings, probe);

        Assert.False(readiness.IsReady);
        Assert.Contains("live but translation capability is still warming", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("start your managed local gpu host", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerizedProviderReadiness_CheckTtsForExecutionAsync_ReturnsNotReadyWhenSelectedQwenProviderFailedWarmup()
    {
        var probe = new ContainerizedServiceProbe(_ctx.Log, (_, _, _) => Task.FromResult(new ContainerHealthStatus(
            true,
            true,
            "12.8",
            "http://localhost:8000",
            null,
            new ContainerCapabilitiesSnapshot(
                TranscriptionReady: true,
                TranscriptionDetail: null,
                TranslationReady: true,
                TranslationDetail: null,
                TtsReady: true,
                TtsDetail: null,
                TtsProviders: new Dictionary<string, bool>
                {
                    [ProviderNames.Qwen] = false,
                },
                TtsProviderDetails: new Dictionary<string, string>
                {
                    [ProviderNames.Qwen] = "Qwen3-TTS warmup failed: out of memory",
                }))));

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv,
            ContainerizedServiceUrl = "http://localhost:8000",
            TtsProvider = ProviderNames.Qwen,
        };

        var readiness = await ContainerizedProviderReadiness.CheckTtsForExecutionAsync(settings, probe);

        Assert.False(readiness.IsReady);
        Assert.Contains("TTS", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerizedProviderReadiness_CheckTtsForExecutionAsync_ReturnsNotReadyWhenProviderNotAdvertised()
    {
        var probe = new ContainerizedServiceProbe(_ctx.Log, (_, _, _) => Task.FromResult(new ContainerHealthStatus(
            true,
            true,
            "12.8",
            "http://localhost:8000",
            null,
            new ContainerCapabilitiesSnapshot(
                TranscriptionReady: true,
                TranscriptionDetail: null,
                TranslationReady: true,
                TranslationDetail: null,
                TtsReady: true,
                TtsDetail: null,
                TtsProviders: new Dictionary<string, bool>
                {
                    [ProviderNames.EdgeTts] = true,
                },
                TtsProviderDetails: new Dictionary<string, string>
                {
                    [ProviderNames.EdgeTts] = "Edge TTS ready",
                }))));

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv,
            ContainerizedServiceUrl = "http://localhost:8000",
            TtsProvider = ProviderNames.Qwen,
        };

        var readiness = await ContainerizedProviderReadiness.CheckTtsForExecutionAsync(settings, probe);

        Assert.False(readiness.IsReady);
        Assert.Contains("not advertised by host", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerizedProviderReadiness_CheckDiarizationForExecutionAsync_UsesProviderSpecificReadiness()
    {
        var probe = new ContainerizedServiceProbe(
            _ctx.Log,
            (_, _, _) => Task.FromResult(new ContainerHealthStatus(
                true,
                true,
                "12.8",
                "http://localhost:8000",
                null,
                new ContainerCapabilitiesSnapshot(
                    TranscriptionReady: true,
                    TranscriptionDetail: null,
                    TranslationReady: true,
                    TranslationDetail: null,
                    TtsReady: true,
                    TtsDetail: null,
                    DiarizationReady: true,
                    DiarizationDetail: "Diarization available",
                    DiarizationProviders: new Dictionary<string, bool>
                    {
                        [ProviderNames.NemoLocal] = true,
                        [ProviderNames.WeSpeakerLocal] = false,
                    },
                    DiarizationProviderDetails: new Dictionary<string, string>
                    {
                        [ProviderNames.NemoLocal] = "NeMo ready",
                        [ProviderNames.WeSpeakerLocal] = "CPU fallback warming",
                    },
                    DiarizationDefaultProvider: ProviderNames.NemoLocal))),
            retryDelay: TimeSpan.FromMilliseconds(10));

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv,
            ContainerizedServiceUrl = AppSettings.ManagedGpuServiceUrl,
            DiarizationProvider = ProviderNames.WeSpeakerLocal,
        };

        var readiness = await ContainerizedProviderReadiness.CheckDiarizationForExecutionAsync(
            settings,
            ProviderNames.WeSpeakerLocal,
            probe,
            new ContainerizedProviderReadiness.ExecutionWaitOptions(
                ExecutionProbeBudget: TimeSpan.FromMilliseconds(40),
                CapabilityWarmupBudget: TimeSpan.FromMilliseconds(80),
                CapabilityWarmupRetryDelay: TimeSpan.FromMilliseconds(10)));

        Assert.False(readiness.IsReady);
        Assert.Contains("live but diarization capability is still warming", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("start your managed local gpu host", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerizedProviderReadiness_CheckDiarization_ReturnsCachedProviderFailureDetailInsteadOfStarting()
    {
        var callCount = 0;
        var releaseRefresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ContainerizedServiceProbe(
            _ctx.Log,
            async (url, _, ct) =>
            {
                var currentCall = Interlocked.Increment(ref callCount);
                if (currentCall == 1)
                {
                    await Task.Delay(10, ct);
                    return new ContainerHealthStatus(
                        true,
                        true,
                        "12.8",
                        url,
                        null,
                        new ContainerCapabilitiesSnapshot(
                            TranscriptionReady: true,
                            TranscriptionDetail: null,
                            TranslationReady: true,
                            TranslationDetail: null,
                            TtsReady: true,
                            TtsDetail: null,
                            DiarizationReady: false,
                            DiarizationDetail: "NeMo diarization config contract invalid: Key 'device' is not in struct",
                            DiarizationProviders: new Dictionary<string, bool>
                            {
                                [ProviderNames.NemoLocal] = false,
                            },
                            DiarizationProviderDetails: new Dictionary<string, string>
                            {
                                [ProviderNames.NemoLocal] = "NeMo diarization config contract invalid: Key 'device' is not in struct",
                            },
                            DiarizationDefaultProvider: ProviderNames.NemoLocal));
                }

                await releaseRefresh.Task.WaitAsync(ct);
                return new ContainerHealthStatus(
                    true,
                    true,
                    "12.8",
                    url,
                    null,
                    new ContainerCapabilitiesSnapshot(
                        TranscriptionReady: true,
                        TranscriptionDetail: null,
                        TranslationReady: true,
                        TranslationDetail: null,
                        TtsReady: true,
                        TtsDetail: null,
                        DiarizationReady: false,
                        DiarizationDetail: "NeMo diarization config contract invalid: Key 'device' is not in struct",
                        DiarizationProviders: new Dictionary<string, bool>
                        {
                            [ProviderNames.NemoLocal] = false,
                        },
                        DiarizationProviderDetails: new Dictionary<string, string>
                        {
                            [ProviderNames.NemoLocal] = "NeMo diarization config contract invalid: Key 'device' is not in struct",
                        },
                        DiarizationDefaultProvider: ProviderNames.NemoLocal));
            },
            retryDelay: TimeSpan.FromMilliseconds(10));

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv,
            ContainerizedServiceUrl = "http://localhost:8000",
            DiarizationProvider = ProviderNames.NemoLocal,
        };

        var first = await probe.WaitForProbeAsync(
            AppSettings.ManagedGpuServiceUrl,
            forceRefresh: true,
            waitTimeout: TimeSpan.FromMilliseconds(500));

        Assert.Equal(ContainerizedProbeState.Available, first.State);
        await Task.Delay(25);

        var cached = probe.GetCurrentOrStartBackgroundProbe(AppSettings.ManagedGpuServiceUrl);
        Assert.Equal(ContainerizedProbeState.Available, cached.State);
        Assert.True(cached.WasCacheHit);

        ExpireCachedProbeResult(probe, AppSettings.ManagedGpuServiceUrl);

        var readiness = ContainerizedProviderReadiness.CheckDiarization(settings, ProviderNames.NemoLocal, probe);
        await WaitForCallCountAsync(() => Volatile.Read(ref callCount), expectedMinimum: 2);

        Assert.False(readiness.IsReady);
        Assert.Contains("NeMo diarization config contract invalid", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("starting", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(callCount >= 2);

        releaseRefresh.SetResult(true);
        await Task.Delay(50);
    }

    [Fact]
    public async Task ContainerizedProviderReadiness_CheckDiarizationForExecutionAsync_ReturnsNotReadyWhenProviderNotAdvertised()
    {
        var probe = new ContainerizedServiceProbe(_ctx.Log, (_, _, _) => Task.FromResult(new ContainerHealthStatus(
            true,
            true,
            "12.8",
            "http://localhost:8000",
            null,
            new ContainerCapabilitiesSnapshot(
                TranscriptionReady: true,
                TranscriptionDetail: null,
                TranslationReady: true,
                TranslationDetail: null,
                TtsReady: true,
                TtsDetail: null,
                DiarizationReady: true,
                DiarizationDetail: null,
                DiarizationProviders: new Dictionary<string, bool>
                {
                    [ProviderNames.NemoLocal] = true,
                },
                DiarizationProviderDetails: new Dictionary<string, string>
                {
                    [ProviderNames.NemoLocal] = "NeMo ready",
                },
                DiarizationDefaultProvider: ProviderNames.NemoLocal))));

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv,
            ContainerizedServiceUrl = AppSettings.ManagedGpuServiceUrl,
            DiarizationProvider = ProviderNames.WeSpeakerLocal,
        };

        var readiness = await ContainerizedProviderReadiness.CheckDiarizationForExecutionAsync(settings, ProviderNames.WeSpeakerLocal, probe);

        Assert.False(readiness.IsReady);
        Assert.Contains("not advertised by host", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FakeDiarizationProvider_RespectsCancellationToken()
    {
        var provider = new FakeDiarizationProvider();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            provider.EnsureReadyAsync(new AppSettings(), ct: cts.Token));

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            provider.DiarizeAsync(new DiarizationRequest("audio.wav"), cts.Token));

        Assert.Null(provider.LastRequest);
    }

    [Fact]
    public async Task ContainerizedProviderReadiness_CheckTtsForExecutionAsync_WaitsForActiveWarmingThenReturnsReady()
    {
        // Simulates Qwen3-TTS loading: first probe returns "warming up" (active warmup),
        // second probe returns ready. Expects the method to retry and ultimately return ready.
        var callCount = 0;
        var probe = new ContainerizedServiceProbe(
            _ctx.Log,
            (_, _, _) =>
            {
                callCount++;
                var ready = callCount >= 2;
                return Task.FromResult(new ContainerHealthStatus(
                    true,
                    true,
                    "12.8",
                    "http://localhost:8000",
                    null,
                    new ContainerCapabilitiesSnapshot(
                        TranscriptionReady: true,
                        TranscriptionDetail: null,
                        TranslationReady: true,
                        TranslationDetail: null,
                        TtsReady: ready,
                        TtsDetail: ready ? null : "Qwen3-TTS warming up",
                        TtsProviders: new Dictionary<string, bool> { [ProviderNames.Qwen] = ready },
                        TtsProviderDetails: ready
                            ? new Dictionary<string, string>()
                            : new Dictionary<string, string> { [ProviderNames.Qwen] = "Qwen3-TTS warming up" })));
            },
            retryDelay: TimeSpan.FromMilliseconds(10));

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv,
            ContainerizedServiceUrl = "http://localhost:8000",
            TtsProvider = ProviderNames.Qwen,
        };

        var readiness = await ContainerizedProviderReadiness.CheckTtsForExecutionAsync(
            settings,
            probe,
            new ContainerizedProviderReadiness.ExecutionWaitOptions(
                ExecutionProbeBudget: TimeSpan.FromMilliseconds(100),
                CapabilityWarmupBudget: TimeSpan.FromMilliseconds(250),
                CapabilityWarmupRetryDelay: TimeSpan.FromMilliseconds(10)));

        Assert.True(readiness.IsReady);
        Assert.Null(readiness.BlockingReason);
        Assert.True(callCount >= 2, "Expected at least two probe calls during warmup wait.");
    }

    [Fact]
    public async Task ContainerizedProviderReadiness_CheckTtsForExecutionAsync_DoesNotRetryTerminalWarmupFailure()
    {
        // Simulates a terminal failure (paging file OOM). Should not retry — returns not-ready immediately.
        var callCount = 0;
        var probe = new ContainerizedServiceProbe(_ctx.Log, (_, _, _) =>
        {
            callCount++;
            return Task.FromResult(new ContainerHealthStatus(
                true,
                true,
                "12.8",
                "http://localhost:8000",
                null,
                new ContainerCapabilitiesSnapshot(
                    TranscriptionReady: true,
                    TranscriptionDetail: null,
                    TranslationReady: true,
                    TranslationDetail: null,
                    TtsReady: true,
                    TtsDetail: null,
                    TtsProviders: new Dictionary<string, bool> { [ProviderNames.Qwen] = false },
                    TtsProviderDetails: new Dictionary<string, string>
                    {
                        [ProviderNames.Qwen] = "Qwen3-TTS warmup failed: paging file too small",
                    })));
        });

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv,
            ContainerizedServiceUrl = "http://localhost:8000",
            TtsProvider = ProviderNames.Qwen,
        };

        var readiness = await ContainerizedProviderReadiness.CheckTtsForExecutionAsync(settings, probe);

        Assert.False(readiness.IsReady);
        Assert.Contains("paging file", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, callCount);  // exactly one probe — no retries
    }

    private ContainerizedInferenceClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(handler))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        return new ContainerizedInferenceClient("http://localhost:8000", _ctx.Log, httpClient);
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

    private static void ExpireCachedProbeResult(ContainerizedServiceProbe probe, string serviceUrl)
    {
        var entriesField = typeof(ContainerizedServiceProbe).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find _entries field.");

        var entries = entriesField.GetValue(probe)
            ?? throw new InvalidOperationException("ContainerizedServiceProbe entries cache was null.");

        var normalizedUrl = ContainerizedInferenceClient.NormalizeBaseUrl(serviceUrl);
        var tryGetValue = entries.GetType().GetMethod("TryGetValue")
            ?? throw new InvalidOperationException("Could not find TryGetValue on probe cache.");

        var tryGetArgs = new object?[] { normalizedUrl, null };
        var found = (bool)(tryGetValue.Invoke(entries, tryGetArgs) ?? false);
        if (!found)
            throw new InvalidOperationException($"No cached probe entry found for {normalizedUrl}.");

        var entry = tryGetArgs[1] ?? throw new InvalidOperationException("Probe cache entry was null.");
        var expiresProperty = entry.GetType().GetProperty("ExpiresAtUtc")
            ?? throw new InvalidOperationException("Could not find ExpiresAtUtc on probe cache entry.");
        expiresProperty.SetValue(entry, DateTimeOffset.UtcNow.AddSeconds(-1));
    }

    private static async Task WaitForCallCountAsync(Func<int> getCount, int expectedMinimum, int timeoutMs = 500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (getCount() >= expectedMinimum)
                return;

            await Task.Delay(10);
        }
    }
}
