using System;
using System.Linq;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class ContainerizedProbeMetricsTests : IClassFixture<SessionWorkflowTemplateFixture>
{
    private readonly SessionWorkflowTemplateFixture _fixture;

    public ContainerizedProbeMetricsTests(SessionWorkflowTemplateFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void ServiceMetrics_InitialState_IsZero()
    {
        var metrics = new ServiceMetrics("http://localhost:8000");

        Assert.Equal("http://localhost:8000", metrics.ServiceUrl);
        Assert.Equal(0, metrics.TotalProbes);
        Assert.Equal(0, metrics.SuccessfulProbes);
        Assert.Equal(0, metrics.FailedProbes);
        Assert.Equal(0, metrics.CacheHits);
        Assert.Equal(0, metrics.CacheMisses);
        Assert.Equal(0, metrics.Cancellations);
        Assert.Equal(0, metrics.TotalDurationMs);
        Assert.Equal(0, metrics.SuccessRate);
        Assert.Equal(0, metrics.CacheHitRate);
        Assert.Equal(0, metrics.AverageDurationMs);
        Assert.Equal(DateTimeOffset.MinValue, metrics.LastProbeAtUtc);
        Assert.Equal(DateTimeOffset.MinValue, metrics.LastSuccessAtUtc);
        Assert.Null(metrics.LastError);
        Assert.Equal(0, metrics.ConsecutiveFailures);
    }

    [Fact]
    public void ServiceMetrics_RecordSuccess_UpdatesCorrectly()
    {
        var metrics = new ServiceMetrics("http://localhost:8000");
        var duration = TimeSpan.FromMilliseconds(150);

        metrics.RecordSuccess(duration, wasCacheHit: false);

        Assert.Equal(1, metrics.TotalProbes);
        Assert.Equal(1, metrics.SuccessfulProbes);
        Assert.Equal(0, metrics.FailedProbes);
        Assert.Equal(0, metrics.CacheHits);
        Assert.Equal(1, metrics.CacheMisses);
        Assert.Equal(150, metrics.TotalDurationMs);
        Assert.Equal(100, metrics.SuccessRate);
        Assert.Equal(0, metrics.CacheHitRate);
        Assert.Equal(150, metrics.AverageDurationMs);
        Assert.NotEqual(DateTimeOffset.MinValue, metrics.LastProbeAtUtc);
        Assert.NotEqual(DateTimeOffset.MinValue, metrics.LastSuccessAtUtc);
        Assert.Null(metrics.LastError);
        Assert.Equal(0, metrics.ConsecutiveFailures);
    }

    [Fact]
    public void ServiceMetrics_RecordFailure_UpdatesCorrectly()
    {
        var metrics = new ServiceMetrics("http://localhost:8000");

        metrics.RecordFailure("Connection refused", wasCacheHit: true);

        Assert.Equal(1, metrics.TotalProbes);
        Assert.Equal(0, metrics.SuccessfulProbes);
        Assert.Equal(1, metrics.FailedProbes);
        Assert.Equal(1, metrics.CacheHits);
        Assert.Equal(0, metrics.CacheMisses);
        Assert.Equal(0, metrics.SuccessRate);
        Assert.Equal(100, metrics.CacheHitRate);
        Assert.Equal("Connection refused", metrics.LastError);
        Assert.Equal(1, metrics.ConsecutiveFailures);
        Assert.NotEqual(DateTimeOffset.MinValue, metrics.LastProbeAtUtc);
        Assert.Equal(DateTimeOffset.MinValue, metrics.LastSuccessAtUtc);
    }

    [Fact]
    public void ServiceMetrics_ConsecutiveFailures_ResetsOnSuccess()
    {
        var metrics = new ServiceMetrics("http://localhost:8000");

        metrics.RecordFailure("Error 1", false);
        metrics.RecordFailure("Error 2", false);
        Assert.Equal(2, metrics.ConsecutiveFailures);

        metrics.RecordSuccess(TimeSpan.FromMilliseconds(10), false);
        Assert.Equal(0, metrics.ConsecutiveFailures);
        Assert.Null(metrics.LastError);
    }

    [Fact]
    public void ServiceMetrics_AverageDuration_CalculatedOnlyFromSuccesses()
    {
        var metrics = new ServiceMetrics("http://localhost:8000");

        metrics.RecordSuccess(TimeSpan.FromMilliseconds(100), false);
        metrics.RecordFailure("Error", false);
        metrics.RecordSuccess(TimeSpan.FromMilliseconds(200), false);

        // (100 + 200) / 2 = 150
        Assert.Equal(150, metrics.AverageDurationMs);
        Assert.Equal(3, metrics.TotalProbes);
    }

    [Fact]
    public void ServiceMetrics_RecordCancellation_UpdatesCorrectly()
    {
        var metrics = new ServiceMetrics("http://localhost:8000");

        metrics.RecordCancellation();

        Assert.Equal(1, metrics.TotalProbes);
        Assert.Equal(1, metrics.Cancellations);
        Assert.Equal(0, metrics.SuccessfulProbes);
        Assert.Equal(0, metrics.FailedProbes);
        Assert.Equal(0, metrics.CacheHits);
        Assert.Equal(0, metrics.CacheMisses);
        Assert.NotEqual(DateTimeOffset.MinValue, metrics.LastProbeAtUtc);
        Assert.Equal(DateTimeOffset.MinValue, metrics.LastSuccessAtUtc);
    }

    [Fact]
    public void ServiceMetrics_RecordCacheAccess_DoesNotIncrementTotalProbes()
    {
        var metrics = new ServiceMetrics("http://localhost:8000");

        metrics.RecordCacheAccess(wasHit: true);
        metrics.RecordCacheAccess(wasHit: false);

        Assert.Equal(0, metrics.TotalProbes);
        Assert.Equal(1, metrics.CacheHits);
        Assert.Equal(1, metrics.CacheMisses);
        Assert.Equal(50, metrics.CacheHitRate);
    }

    [Fact]
    public void ServiceMetrics_Reset_ClearsAllExceptUrl()
    {
        var metrics = new ServiceMetrics("http://localhost:8000");
        metrics.RecordSuccess(TimeSpan.FromMilliseconds(100), true);
        metrics.RecordFailure("Error", false);
        metrics.RecordCancellation();

        metrics.Reset();

        Assert.Equal("http://localhost:8000", metrics.ServiceUrl);
        Assert.Equal(0, metrics.TotalProbes);
        Assert.Equal(0, metrics.SuccessfulProbes);
        Assert.Equal(0, metrics.FailedProbes);
        Assert.Equal(0, metrics.CacheHits);
        Assert.Equal(0, metrics.CacheMisses);
        Assert.Equal(0, metrics.Cancellations);
        Assert.Equal(0, metrics.TotalDurationMs);
        Assert.Equal(0, metrics.ConsecutiveFailures);
        Assert.Equal(0, metrics.SuccessRate);
        Assert.Equal(0, metrics.CacheHitRate);
        Assert.Equal(0, metrics.AverageDurationMs);
        Assert.Null(metrics.LastError);
        Assert.Equal(DateTimeOffset.MinValue, metrics.LastProbeAtUtc);
        Assert.Equal(DateTimeOffset.MinValue, metrics.LastSuccessAtUtc);
    }

    [Fact]
    public async Task ServiceMetrics_ConcurrentUpdates_AreThreadSafe()
    {
        var metrics = new ServiceMetrics("http://localhost:8000");
        int count = 1000;

        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0)
                metrics.RecordSuccess(TimeSpan.FromMilliseconds(10), i % 4 == 0);
            else
                metrics.RecordFailure("Error", i % 4 == 1);
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(count, metrics.TotalProbes);
        Assert.Equal(count / 2, metrics.SuccessfulProbes);
        Assert.Equal(count / 2, metrics.FailedProbes);
        Assert.Equal(count, metrics.CacheHits + metrics.CacheMisses);
    }

    [Fact]
    public void ContainerizedProbeMetrics_GetOrCreateMetrics_NormalizesUrl()
    {
        var metricsRepo = new ContainerizedProbeMetrics();

        var m1 = metricsRepo.GetOrCreateMetrics("http://localhost:8000/");
        var m2 = metricsRepo.GetOrCreateMetrics("  http://localhost:8000  ");

        Assert.Same(m1, m2);
        Assert.Equal("http://localhost:8000", m1.ServiceUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ContainerizedProbeMetrics_GetOrCreateMetrics_ThrowsOnInvalidUrl(string? url)
    {
        var metricsRepo = new ContainerizedProbeMetrics();
        Assert.Throws<ArgumentException>(() => metricsRepo.GetOrCreateMetrics(url!));
    }

    [Theory]
    [InlineData("ftp://localhost")]
    [InlineData("not-a-url")]
    public void ContainerizedProbeMetrics_GetOrCreateMetrics_ThrowsOnNonHttpUrl(string url)
    {
        var metricsRepo = new ContainerizedProbeMetrics();
        Assert.Throws<ArgumentException>(() => metricsRepo.GetOrCreateMetrics(url));
    }

    [Fact]
    public void ContainerizedProbeMetrics_GetAllMetrics_ReturnsAll()
    {
        var metricsRepo = new ContainerizedProbeMetrics();
        metricsRepo.GetOrCreateMetrics("http://service1:8000");
        metricsRepo.GetOrCreateMetrics("http://service2:8000");

        var all = metricsRepo.GetAllMetrics();

        Assert.Equal(2, all.Length);
        Assert.Contains(all, m => m.ServiceUrl == "http://service1:8000");
        Assert.Contains(all, m => m.ServiceUrl == "http://service2:8000");
    }

    [Fact]
    public void ContainerizedProbeMetrics_RemoveMetrics_RemovesEntry()
    {
        var metricsRepo = new ContainerizedProbeMetrics();
        metricsRepo.GetOrCreateMetrics("http://localhost:8000");

        metricsRepo.RemoveMetrics("http://localhost:8000/");

        Assert.Empty(metricsRepo.GetAllMetrics());
    }

    [Fact]
    public void ContainerizedProbeMetrics_ResetAll_ResetsAllEntries()
    {
        var metricsRepo = new ContainerizedProbeMetrics();
        var m1 = metricsRepo.GetOrCreateMetrics("http://service1:8000");
        var m2 = metricsRepo.GetOrCreateMetrics("http://service2:8000");

        m1.RecordSuccess(TimeSpan.FromMilliseconds(10), false);
        m2.RecordFailure("Error", false);

        metricsRepo.ResetAll();

        Assert.Equal(0, m1.TotalProbes);
        Assert.Equal(0, m2.TotalProbes);
        Assert.Equal(2, metricsRepo.GetAllMetrics().Length);
    }
}