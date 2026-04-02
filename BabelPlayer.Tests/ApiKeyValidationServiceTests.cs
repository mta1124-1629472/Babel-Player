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
    public void GetAvailabilityMessage_ElevenLabs_ReturnsUnavailable_WhenNoImplementedProviderUsesKey()
    {
        var service = CreateService();

        var message = service.GetAvailabilityMessage(CredentialKeys.ElevenLabs);

        Assert.False(string.IsNullOrWhiteSpace(message));
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

    private ApiKeyValidationService CreateService(Func<string, OpenAiApiClient>? openAiClientFactory = null) =>
        new(
            new TranscriptionRegistry(_log),
            new TranslationRegistry(_log),
            new TtsRegistry(_log),
            openAiClientFactory);

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler(request);
    }
}