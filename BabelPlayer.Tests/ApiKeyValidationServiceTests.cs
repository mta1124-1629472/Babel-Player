using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;

namespace BabelPlayer.Tests;

public sealed class ApiKeyValidationServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;

    public ApiKeyValidationServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-api-key-validation-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void GetAvailabilityMessage_OpenAi_ReturnsNull_WhenImplementedProviderUsesKey()
    {
        var service = CreateService();

        var message = service.GetAvailabilityMessage(CredentialKeys.OpenAi);

        Assert.Null(message);
    }

    [Fact]
    public void GetAvailabilityMessage_DeepL_ReturnsNull_WhenImplementedProviderUsesKey()
    {
        var service = CreateService();

        var message = service.GetAvailabilityMessage(CredentialKeys.Deepl);

        Assert.Null(message);
    }

    [Fact]
    public void GetAvailabilityMessage_ElevenLabs_ReturnsNull_WhenImplementedProviderUsesKey()
    {
        var service = CreateService();

        var message = service.GetAvailabilityMessage(CredentialKeys.ElevenLabs);

        Assert.Null(message);
    }

    [Fact]
    public async Task ValidateAsync_OpenAi_ReturnsSuccess_WhenSupportedModelIsAvailable()
    {
                var service = CreateService(_ => new OpenAiApiClient("test-key", new StubHttpMessageHandler(_ =>
                        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                                Content = new StringContent("""
                                {
                                    "object": "list",
                                    "data": [
                                        { "id": "gpt-4o-mini", "object": "model", "created": 1, "owned_by": "openai" }
                                    ]
                                }
                                """, Encoding.UTF8, "application/json")
                        }))));

                var result = await service.ValidateAsync(CredentialKeys.OpenAi, "test-key");

        Assert.True(result.IsAvailable);
        Assert.True(result.IsValid);
        Assert.Contains("gpt-4o-mini", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_OpenAi_ReturnsFailure_WhenKeyUnauthorized()
    {
                var service = CreateService(_ => new OpenAiApiClient("bad-key", new StubHttpMessageHandler(_ =>
                        Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                        {
                                Content = new StringContent("""
                                {
                                    "error": {
                                        "message": "Incorrect API key provided."
                                    }
                                }
                                """, Encoding.UTF8, "application/json")
                        }))));

                var result = await service.ValidateAsync(CredentialKeys.OpenAi, "bad-key");

        Assert.True(result.IsAvailable);
        Assert.False(result.IsValid);
        Assert.Contains("rejected", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_OpenAi_ReturnsFailure_WhenNoSupportedModelsAvailable()
    {
                var service = CreateService(_ => new OpenAiApiClient("test-key", new StubHttpMessageHandler(_ =>
                        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                                Content = new StringContent("""
                                {
                                    "object": "list",
                                    "data": [
                                        { "id": "other-model", "object": "model", "created": 1, "owned_by": "openai" }
                                    ]
                                }
                                """, Encoding.UTF8, "application/json")
                        }))));

                var result = await service.ValidateAsync(CredentialKeys.OpenAi, "test-key");

        Assert.True(result.IsAvailable);
        Assert.False(result.IsValid);
        Assert.Contains("supported", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_ElevenLabs_ReturnsSuccess_WhenSubscriptionEndpointAccessible()
    {
        var service = CreateService(
            elevenLabsClientFactory: _ => new ElevenLabsApiClient("eleven-key", new StubHttpMessageHandler(_ =>
                System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "tier": "starter",
                      "character_count": 5000,
                      "character_limit": 30000
                    }
                    """, Encoding.UTF8, "application/json")
                }))));

        var result = await service.ValidateAsync(CredentialKeys.ElevenLabs, "eleven-key");

        Assert.True(result.IsAvailable);
        Assert.True(result.IsValid);
        Assert.Contains("starter", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5000", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_ElevenLabs_ReturnsFailure_WhenUnauthorized()
    {
        var service = CreateService(
            elevenLabsClientFactory: _ => new ElevenLabsApiClient("bad-key", new StubHttpMessageHandler(_ =>
                System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("unauthorized", Encoding.UTF8, "text/plain")
                }))));

        var result = await service.ValidateAsync(CredentialKeys.ElevenLabs, "bad-key");

        Assert.True(result.IsAvailable);
        Assert.False(result.IsValid);
        Assert.Contains("rejected", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_DeepL_ReturnsSuccess_WhenUsageEndpointAccessible()
    {
        var service = CreateService(
            deepLClientFactory: _ => new DeepLApiClient("deepl-key", new StubHttpMessageHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "character_count": 42,
                      "character_limit": 1250000
                    }
                    """, Encoding.UTF8, "application/json")
                }))));

        var result = await service.ValidateAsync(CredentialKeys.Deepl, "deepl-key");

        Assert.True(result.IsAvailable);
        Assert.True(result.IsValid);
        Assert.Contains("42", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_DeepL_ReturnsFailure_WhenUnauthorized()
    {
        var service = CreateService(
            deepLClientFactory: _ => new DeepLApiClient("bad-deepl-key", new StubHttpMessageHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("forbidden", Encoding.UTF8, "text/plain")
                }))));

        var result = await service.ValidateAsync(CredentialKeys.Deepl, "bad-deepl-key");

        Assert.True(result.IsAvailable);
        Assert.False(result.IsValid);
        Assert.Contains("rejected", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetAvailabilityMessage_GoogleAi_ReturnsNull_WhenDirectProbeAvailable()
    {
        var service = CreateService();

        var message = service.GetAvailabilityMessage(CredentialKeys.GoogleAi);

        Assert.Null(message);
    }

    [Fact]
    public async Task ValidateAsync_Google_ReturnsSuccess_WhenVoicesEndpointAccessible()
    {
        var voicesJson = """
        {
          "voices": [
            { "name": "en-US-Standard-A" },
            { "name": "en-US-Standard-B" }
          ]
        }
        """;
        var service = CreateService(
            googleClientFactory: _ => new GoogleApiClient("google-key", new StubHttpMessageHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(voicesJson, Encoding.UTF8, "application/json")
                }))));

        var result = await service.ValidateAsync(CredentialKeys.GoogleAi, "google-key");

        Assert.True(result.IsAvailable);
        Assert.True(result.IsValid);
        Assert.Contains("2", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_Google_ReturnsFailure_WhenForbidden()
    {
        var errorJson = """
        {
          "error": {
            "code": 403,
            "message": "API key not valid. Please pass a valid API key.",
            "status": "PERMISSION_DENIED"
          }
        }
        """;
        var service = CreateService(
            googleClientFactory: _ => new GoogleApiClient("bad-google-key", new StubHttpMessageHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(errorJson, Encoding.UTF8, "application/json")
                }))));

        var result = await service.ValidateAsync(CredentialKeys.GoogleAi, "bad-google-key");

        Assert.True(result.IsAvailable);
        Assert.False(result.IsValid);
        Assert.Contains("rejected", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private ApiKeyValidationService CreateService(
        Func<string, OpenAiApiClient>? openAiClientFactory = null,
        Func<string, DeepLApiClient>? deepLClientFactory = null,
        Func<string, ElevenLabsApiClient>? elevenLabsClientFactory = null,
        Func<string, GoogleApiClient>? googleClientFactory = null,
        HttpClient? hfHttpClient = null) =>
        new(
            transcriptionRegistry: new TranscriptionRegistry(_log),
            translationRegistry: new TranslationRegistry(_log),
            ttsRegistry: new TtsRegistry(_log),
            openAiClientFactory: openAiClientFactory,
            deepLClientFactory: deepLClientFactory,
            elevenLabsClientFactory: elevenLabsClientFactory,
            googleClientFactory: googleClientFactory,
            hfHttpClient: hfHttpClient);

    // ── HuggingFace helpers ───────────────────────────────────────────────────

    // Minimum-length valid-looking HF token for test purposes.
    private static string ValidHfTokenShape => "hf_" + new string('x', 30 - "hf_".Length); // exactly 30 chars

    private ApiKeyValidationService CreateHfService(
        Func<HttpRequestMessage, HttpResponseMessage>? whoamiResp = null,
        Func<HttpRequestMessage, HttpResponseMessage>? modelProbeResp = null)
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("whoami"))
                return Task.FromResult(whoamiResp?.Invoke(req)
                    ?? new HttpResponseMessage(HttpStatusCode.OK));
            return Task.FromResult(modelProbeResp?.Invoke(req)
                ?? new HttpResponseMessage(HttpStatusCode.OK));
        });
        return new ApiKeyValidationService(
            transcriptionRegistry: new TranscriptionRegistry(_log),
            translationRegistry: new TranslationRegistry(_log),
            ttsRegistry: new TtsRegistry(_log),
            hfHttpClient: new HttpClient(handler));
    }

    // ── HuggingFace format pre-check ──────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_HuggingFace_ReturnsFailure_WhenTokenMissingPrefix()
    {
        var service = CreateHfService();
        var result = await service.ValidateAsync(CredentialKeys.HuggingFace, "notahftoken12345678901234567890");

        Assert.True(result.IsAvailable);
        Assert.False(result.IsValid);
        Assert.Contains("hf_", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_HuggingFace_ReturnsFailure_WhenTokenTooShort()
    {
        var service = CreateHfService();
        var result = await service.ValidateAsync(CredentialKeys.HuggingFace, "hf_short");

        Assert.True(result.IsAvailable);
        Assert.False(result.IsValid);
        Assert.Contains("30", result.Message, StringComparison.Ordinal);
    }

    // ── HuggingFace whoami step ───────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_HuggingFace_ReturnsFailure_WhenWhoami401()
    {
        var service = CreateHfService(
            whoamiResp: _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await service.ValidateAsync(CredentialKeys.HuggingFace, ValidHfTokenShape);

        Assert.True(result.IsAvailable);
        Assert.False(result.IsValid);
        Assert.Contains("401", result.Message, StringComparison.Ordinal);
        Assert.Contains("revoked", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_HuggingFace_ReturnsFailure_WhenWhoamiUnexpectedStatus()
    {
        var service = CreateHfService(
            whoamiResp: _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await service.ValidateAsync(CredentialKeys.HuggingFace, ValidHfTokenShape);

        Assert.True(result.IsAvailable);
        Assert.False(result.IsValid);
        Assert.Contains("503", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_HuggingFace_ReturnsFailure_WhenWhoamiNetworkError()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));
        var service = new ApiKeyValidationService(
            transcriptionRegistry: new TranscriptionRegistry(_log),
            translationRegistry: new TranslationRegistry(_log),
            ttsRegistry: new TtsRegistry(_log),
            hfHttpClient: new HttpClient(handler));

        var result = await service.ValidateAsync(CredentialKeys.HuggingFace, ValidHfTokenShape);

        Assert.True(result.IsAvailable);
        Assert.False(result.IsValid);
        Assert.Contains("connection refused", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── HuggingFace model-probe step ──────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_HuggingFace_ReturnsFailure_WhenModelProbe401()
    {
        var service = CreateHfService(
            modelProbeResp: _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await service.ValidateAsync(CredentialKeys.HuggingFace, ValidHfTokenShape);

        Assert.True(result.IsAvailable);
        Assert.False(result.IsValid);
        Assert.Contains("pyannote", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("license", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_HuggingFace_ReturnsFailure_WhenModelProbe403()
    {
        var service = CreateHfService(
            modelProbeResp: _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await service.ValidateAsync(CredentialKeys.HuggingFace, ValidHfTokenShape);

        Assert.True(result.IsAvailable);
        Assert.False(result.IsValid);
        Assert.Contains("pyannote", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_HuggingFace_ReturnsFailure_WhenModelProbeUnexpectedStatus()
    {
        var service = CreateHfService(
            modelProbeResp: _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await service.ValidateAsync(CredentialKeys.HuggingFace, ValidHfTokenShape);

        Assert.True(result.IsAvailable);
        Assert.False(result.IsValid);
        Assert.Contains("502", result.Message, StringComparison.Ordinal);
    }

    // ── HuggingFace success path ──────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_HuggingFace_ReturnsSuccess_WhenBothProbesSucceed()
    {
        var service = CreateHfService(); // both default to 200 OK

        var result = await service.ValidateAsync(CredentialKeys.HuggingFace, ValidHfTokenShape);

        Assert.True(result.IsAvailable);
        Assert.True(result.IsValid);
        Assert.Contains("pyannote", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("diarization", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── HuggingFace cancellation ──────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_HuggingFace_PropagatesCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new StubHttpMessageHandler(_ =>
            Task.FromException<HttpResponseMessage>(new OperationCanceledException(cts.Token)));
        var service = new ApiKeyValidationService(
            transcriptionRegistry: new TranscriptionRegistry(_log),
            translationRegistry: new TranslationRegistry(_log),
            ttsRegistry: new TtsRegistry(_log),
            hfHttpClient: new HttpClient(handler));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ValidateAsync(CredentialKeys.HuggingFace, ValidHfTokenShape, cts.Token));
    }

    // ── GetAvailabilityMessage for HuggingFace ────────────────────────────────

    [Fact]
    public void GetAvailabilityMessage_HuggingFace_ReturnsNull_WhenDirectProbeAvailable()
    {
        var service = CreateService();

        var message = service.GetAvailabilityMessage(CredentialKeys.HuggingFace);

        Assert.Null(message);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _handler(request);
        }
    }
}