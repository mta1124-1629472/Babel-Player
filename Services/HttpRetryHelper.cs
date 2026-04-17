using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Shared retry helper for outbound cloud <see cref="HttpClient"/> calls.
/// Retries transient status codes (408, 429, 5xx) and transient network
/// exceptions with exponential backoff, honoring <c>Retry-After</c> when
/// present. Each attempt must build a fresh <see cref="HttpRequestMessage"/>
/// and <see cref="HttpContent"/> inside the delegate because HTTP requests
/// and content are single-use.
/// </summary>
public static class HttpRetryHelper
{
    private const int DefaultMaxAttempts = 3;

    public static async Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> sendAsync,
        int maxAttempts = DefaultMaxAttempts,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        CancellationToken cancellationToken = default)
    {
        delayAsync ??= Task.Delay;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var response = await sendAsync().ConfigureAwait(false);
                if (attempt >= maxAttempts || !ShouldRetry(response.StatusCode))
                    return response;

                var delay = GetDelay(response, attempt);
                response.Dispose();
                await delayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex, cancellationToken))
            {
                var delay = GetDelay(attempt);
                await delayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout
        || statusCode == HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or IOException or TimeoutException
        || (ex is OperationCanceledException oce && oce.CancellationToken != cancellationToken);

    private static TimeSpan GetDelay(HttpResponseMessage response, int attempt) =>
        response.Headers.RetryAfter?.Delta ?? GetDelay(attempt);

    private static TimeSpan GetDelay(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Pow(2, attempt - 1) * 200);
}
