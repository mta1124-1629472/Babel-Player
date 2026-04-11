using System;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public enum ContainerizedRequestKind
{
    Other,
    Transcription,
    Translation,
    Tts,
    Qwen,
    Diarization,
}

/// <summary>
/// Tracks in-flight requests against the managed inference host so lifecycle code
/// can avoid restarting the host while local work is still active.
/// </summary>
public sealed class ContainerizedRequestLeaseTracker
{
    private int _activeRequests;
    private int _activeQwenRequests;
    private int _activeDiarizationRequests;
    private int _isRecovering;
    private readonly SemaphoreSlim _zeroRequestsSignal = new(0, 1);

    public int ActiveRequests => Volatile.Read(ref _activeRequests);

    public int ActiveQwenRequests => Volatile.Read(ref _activeQwenRequests);

    public int ActiveDiarizationRequests => Volatile.Read(ref _activeDiarizationRequests);

    public bool HasActiveRequests => ActiveRequests > 0;

    /// <summary>
    /// Reserves an active request slot for the specified request kind and returns a lease that releases that reservation when disposed.
    /// </summary>
    /// <param name="kind">The category of the request (for example: Qwen, Diarization, Transcription).</param>
    /// <returns>An <see cref="IDisposable"/> lease that, when disposed, releases the reserved request slot for the given kind; returns null when recovery is in progress and new leases are blocked.</returns>
    public IDisposable? Acquire(ContainerizedRequestKind kind)
    {
        // Reject new leases while recovering to prevent TOCTOU races.
        if (Volatile.Read(ref _isRecovering) == 1)
            return null;

        Interlocked.Increment(ref _activeRequests);

        switch (kind)
        {
            case ContainerizedRequestKind.Qwen:
                Interlocked.Increment(ref _activeQwenRequests);
                break;
            case ContainerizedRequestKind.Diarization:
                Interlocked.Increment(ref _activeDiarizationRequests);
                break;
        }

        return new Lease(this, kind);
    }

    /// <summary>
    /// Begins a recovery lifecycle gate that blocks new lease acquisitions and returns a token to end recovery.
    /// </summary>
    /// <returns>An <see cref="IDisposable"/> token that, when disposed, re-enables lease acquisitions.</returns>
    public IDisposable BeginRecovery()
    {
        Interlocked.Exchange(ref _isRecovering, 1);
        return new RecoveryToken(this);
    }

    /// <summary>
    /// Waits until all active requests have completed or the cancellation token is triggered.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the wait operation.</param>
    /// <returns>A task that completes when the active request count reaches zero.</returns>
    public async Task WaitForZeroActiveRequestsAsync(CancellationToken cancellationToken = default)
    {
        while (ActiveRequests > 0)
        {
            try
            {
                await _zeroRequestsSignal.WaitAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Timeout is expected; loop and check count again.
            }
        }
    }

    /// <summary>
    /// Ends the recovery gate and re-enables lease acquisitions.
    /// </summary>
    private void EndRecovery()
    {
        Interlocked.Exchange(ref _isRecovering, 0);
    }

    /// <summary>
    /// Releases a previously acquired request slot for the specified request kind by decrementing the total active request count and the corresponding kind-specific counter when applicable.
    /// </summary>
    /// <param name="kind">The category of the request whose counters should be decremented.</param>
    private void Release(ContainerizedRequestKind kind)
    {
        switch (kind)
        {
            case ContainerizedRequestKind.Qwen:
                Interlocked.Decrement(ref _activeQwenRequests);
                break;
            case ContainerizedRequestKind.Diarization:
                Interlocked.Decrement(ref _activeDiarizationRequests);
                break;
        }

        var newCount = Interlocked.Decrement(ref _activeRequests);

        // Signal waiters when count reaches zero.
        if (newCount == 0)
        {
            try
            {
                _zeroRequestsSignal.Release();
            }
            catch
            {
                // Already signaled; ignore.
            }
        }
    }

    private sealed class Lease(ContainerizedRequestLeaseTracker owner, ContainerizedRequestKind kind) : IDisposable
    {
        private ContainerizedRequestLeaseTracker? _owner = owner;

        /// <summary>
        /// Releases the acquired request slot if it has not already been released.
        /// </summary>
        /// <remarks>
        /// Disposal is idempotent and thread-safe; the owner's <c>Release</c> is invoked at most once for this lease's request kind.
        /// </remarks>
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(kind);
        }
    }

    private sealed class RecoveryToken(ContainerizedRequestLeaseTracker owner) : IDisposable
    {
        private ContainerizedRequestLeaseTracker? _owner = owner;

        /// <summary>
        /// Ends the recovery gate when disposed.
        /// </summary>
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndRecovery();
        }
    }
}