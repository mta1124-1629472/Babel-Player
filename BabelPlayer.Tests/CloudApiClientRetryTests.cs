using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class CloudApiClientRetryTests
{
    [Fact]
    public async Task OpenAiApiClient_ListModelsAsync_RetriesTooManyRequests()
    {
        var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":{\"message\":\"slow down\"}}", Encoding.UTF8, "application/json")
        };
        retry.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(System.TimeSpan.Zero);

        var handler = new SequencedHandler(
            retry,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"gpt-4o-mini\"}]}", Encoding.UTF8, "application/json")
            });

        using var client = new OpenAiApiClient("test-key", handler);
        var models = await client.ListModelsAsync();

        Assert.Equal(2, handler.CallCount);
        Assert.Contains("gpt-4o-mini", models);
    }

    [Fact]
    public async Task DeepLApiClient_GetUsageAsync_RetriesServiceUnavailable()
    {
        var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("busy")
        };
        unavailable.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(System.TimeSpan.Zero);

        var handler = new SequencedHandler(
            unavailable,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"character_count\":1,\"character_limit\":1000}", Encoding.UTF8, "application/json")
            });

        using var client = new DeepLApiClient("deepl-key", handler);
        var usage = await client.GetUsageAsync();

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(1, usage.CharacterCount);
    }

    [Fact]
    public async Task ElevenLabsApiClient_GetSubscriptionAsync_RetriesTooManyRequests()
    {
        var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("retry")
        };
        retry.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(System.TimeSpan.Zero);

        var handler = new SequencedHandler(
            retry,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"tier\":\"starter\",\"character_count\":5,\"character_limit\":10}", Encoding.UTF8, "application/json")
            });

        using var client = new ElevenLabsApiClient("eleven-key", handler);
        var subscription = await client.GetSubscriptionAsync();

        Assert.Equal(2, handler.CallCount);
        Assert.Equal("starter", subscription.Tier);
    }

    [Fact]
    public async Task GoogleApiClient_ListVoicesAsync_RetriesServiceUnavailable()
    {
        var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":{\"message\":\"busy\"}}", Encoding.UTF8, "application/json")
        };
        unavailable.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(System.TimeSpan.Zero);

        var handler = new SequencedHandler(
            unavailable,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"voices\":[{\"name\":\"en-US-Standard-A\"}]}", Encoding.UTF8, "application/json")
            });

        using var client = new GoogleApiClient("google-key", handler);
        var voices = await client.ListVoicesAsync();

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(1, voices.Count);
    }

    [Fact]
    public async Task GeminiApiClient_GenerateTextAsync_RetriesTooManyRequests()
    {
        var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"slow down\"}", Encoding.UTF8, "application/json")
        };
        retry.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(System.TimeSpan.Zero);

        var handler = new SequencedHandler(
            retry,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"translated\"}]}}]}", Encoding.UTF8, "application/json")
            });

        using var client = new GeminiApiClient("gemini-key", handler);
        var text = await client.GenerateTextAsync("gemini-2.5-flash", "system", "prompt");

        Assert.Equal(2, handler.CallCount);
        Assert.Equal("translated", text);
    }

    private sealed class SequencedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
