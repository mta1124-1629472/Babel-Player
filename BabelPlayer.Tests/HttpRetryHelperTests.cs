using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class HttpRetryHelperTests
{
    [Fact]
    public async Task SendAsync_RetriesTransientStatusCodes_UpToThreeAttempts()
    {
        var attempt = 0;

        using var response = await HttpRetryHelper.SendAsync(
            async () =>
            {
                attempt++;
                await Task.Yield();
                return attempt < 3
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            },
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.Equal(3, attempt);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_UsesRetryAfterHeader_WhenPresent()
    {
        TimeSpan? capturedDelay = null;

        using var response = await HttpRetryHelper.SendAsync(
            () =>
            {
                var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                retry.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return Task.FromResult(retry);
            },
            maxAttempts: 2,
            delayAsync: (delay, _) =>
            {
                capturedDelay = delay;
                return Task.CompletedTask;
            });

        Assert.Equal(TimeSpan.FromSeconds(2), capturedDelay);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_DoesNotRetrySuccess()
    {
        var attempt = 0;

        using var response = await HttpRetryHelper.SendAsync(
            () =>
            {
                attempt++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            },
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.Equal(1, attempt);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_RetriesTransientException_ThenReturnsSuccess()
    {
        var attempt = 0;

        using var response = await HttpRetryHelper.SendAsync(
            () =>
            {
                attempt++;
                if (attempt == 1)
                    throw new HttpRequestException("transient");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            },
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.Equal(2, attempt);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            HttpRetryHelper.SendAsync(
                () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
                delayAsync: static (_, _) => Task.CompletedTask,
                cancellationToken: cts.Token));
    }
}
