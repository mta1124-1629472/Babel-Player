using System;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

internal sealed class ProviderLease<TProvider> : IAsyncDisposable where TProvider : class
{
    private ProviderLeaseManager<TProvider>? _owner;
    private ProviderLeaseManager<TProvider>.Entry? _entry;

    internal ProviderLease(
        ProviderLeaseManager<TProvider> owner,
        ProviderLeaseManager<TProvider>.Entry entry)
    {
        _owner = owner;
        _entry = entry;
        LeaseId = Guid.NewGuid();
        CacheGeneration = entry.CacheGeneration;
        ProviderId = entry.ProviderId;
        Provider = entry.Provider;
    }

    public Guid LeaseId { get; }

    public long CacheGeneration { get; }

    public string ProviderId { get; }

    public TProvider Provider { get; }

    public ValueTask DisposeAsync()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        var entry = Interlocked.Exchange(ref _entry, null);
        if (owner is not null && entry is not null)
            owner.Release(entry);

        return ValueTask.CompletedTask;
    }
}

internal sealed class ProviderLeaseManager<TProvider> where TProvider : class
{
    internal sealed class Entry
    {
        public required TProvider Provider { get; init; }
        public required string ProviderId { get; init; }
        public required long CacheGeneration { get; init; }
        public int RefCount;
        public bool Retired;
        public bool Disposed;
    }

    private readonly object _gate = new();
    private readonly AppLog _log;
    private readonly string _providerLabel;
    private Entry? _current;
    private long _nextGeneration;

    internal ProviderLeaseManager(AppLog log, string providerLabel)
    {
        _log = log;
        _providerLabel = providerLabel;
    }

    public TProvider? CurrentProvider
    {
        get
        {
            lock (_gate)
                return _current?.Provider;
        }
    }

    public ProviderLease<TProvider> AcquireOrCreate(Func<TProvider> factory, string providerId)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        lock (_gate)
        {
            if (_current is null)
            {
                _current = new Entry
                {
                    Provider = factory(),
                    ProviderId = providerId,
                    CacheGeneration = Interlocked.Increment(ref _nextGeneration),
                };
            }

            _current.RefCount++;
            return new ProviderLease<TProvider>(this, _current);
        }
    }

    public void RetireCurrent(string reason)
    {
        Entry? toDispose = null;
        lock (_gate)
        {
            if (_current is null)
                return;

            _current.Retired = true;
            if (_current.RefCount == 0)
            {
                _current.Disposed = true;
                toDispose = _current;
            }

            _current = null;
        }

        if (toDispose is not null)
            DisposeEntry(toDispose, reason);
    }

    internal void Release(Entry entry)
    {
        Entry? toDispose = null;
        lock (_gate)
        {
            if (entry.RefCount > 0)
                entry.RefCount--;

            if (entry.RefCount == 0 && entry.Retired && !entry.Disposed)
            {
                entry.Disposed = true;
                toDispose = entry;
            }
        }

        if (toDispose is not null)
            DisposeEntry(toDispose, "lease released");
    }

    private void DisposeEntry(Entry entry, string reason)
    {
        try
        {
            switch (entry.Provider)
            {
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
                case IAsyncDisposable asyncDisposable:
                    Task.Run(() => asyncDisposable.DisposeAsync().AsTask())
                        .GetAwaiter()
                        .GetResult();
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"Retired {_providerLabel} provider '{entry.ProviderId}' generation {entry.CacheGeneration} disposal failed after {reason}: {ex.Message}");
        }
    }
}
