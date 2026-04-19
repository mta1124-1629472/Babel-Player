using System;
using System.IO;
using System.Threading.Tasks;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class ProviderLeaseTests
{
    [Fact]
    public async Task ProviderLeaseManager_RetiresProviderAfterLastLeaseReleases()
    {
        using var log = new AppLog(Path.Combine(Path.GetTempPath(), $"provider-lease-{Guid.NewGuid():N}.log"));
        var manager = new ProviderLeaseManager<TestDisposableProvider>(log, "test");
        var provider = new TestDisposableProvider();

        var leaseA = manager.AcquireOrCreate(() => provider, "provider-a");
        var leaseB = manager.AcquireOrCreate(() => provider, "provider-a");

        manager.RetireCurrent("settings changed");
        Assert.False(provider.Disposed);

        await leaseA.DisposeAsync();
        Assert.False(provider.Disposed);

        await leaseB.DisposeAsync();
        Assert.True(provider.Disposed);
    }

    [Fact]
    public async Task ProviderLeaseManager_DisposesAsyncProviderWhenRetired()
    {
        using var log = new AppLog(Path.Combine(Path.GetTempPath(), $"provider-lease-{Guid.NewGuid():N}.log"));
        var manager = new ProviderLeaseManager<TestAsyncDisposableProvider>(log, "test");
        var provider = new TestAsyncDisposableProvider();

        var lease = manager.AcquireOrCreate(() => provider, "provider-a");
        manager.RetireCurrent("settings changed");
        Assert.False(provider.Disposed);

        await lease.DisposeAsync();

        Assert.True(provider.Disposed);
    }

    private sealed class TestDisposableProvider : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class TestAsyncDisposableProvider : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
