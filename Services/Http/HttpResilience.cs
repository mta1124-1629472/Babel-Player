using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services.Http;

/// <summary>
/// Small, dependency-free retry helper for cloud HTTP calls (transient network / timeout).
/// Does not retry after a response body has been consumed unless the caller structures work that way.
/// </summary>
public static class HttpResilience
{
    private const int DefaultMaxAttempts = 3;

    public static Task ExecuteAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default,
        int maxAttempts = DefaultMaxAttempts) =>
        ExecuteCoreAsync(operation, cancellationToken, maxAttempts);

    public static Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default,
        int maxAttempts = DefaultMaxAttempts) =>
        ExecuteAsync(operation, static () => true, cancellationToken, maxAttempts);

    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        Func<bool> shouldRetry,
        CancellationToken cancellationToken = default,
        int maxAttempts = DefaultMaxAttempts)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts && shouldRetry() && IsTransientNetworkFailure(ex))
            {
                last = ex;
                await Task.Delay(ComputeBackoffDelayMs(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException("HTTP resilience exhausted without a captured exception.");
    }

    private static async Task ExecuteCoreAsync(
        Func<Task> operation,
        CancellationToken cancellationToken,
        int maxAttempts)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await operation().ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientNetworkFailure(ex))
            {
                last = ex;
                await Task.Delay(ComputeBackoffDelayMs(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException("HTTP resilience exhausted without a captured exception.");
    }

    private static bool IsTransientNetworkFailure(Exception ex) =>
        ex is HttpRequestException
        or IOException
        or TimeoutException
        or TaskCanceledException;

    private static int ComputeBackoffDelayMs(int attempt)
    {
        var jitter = Random.Shared.Next(0, 120);
        return attempt switch
        {
            1 => 200 + jitter,
            2 => 600 + jitter,
            _ => 1200 + jitter,
        };
    }
}
