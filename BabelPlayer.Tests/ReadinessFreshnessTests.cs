using System;
using System.IO;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;

namespace BabelPlayer.Tests;

public sealed class ReadinessFreshnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-readiness-tests-{Guid.NewGuid():N}");
    private readonly AppLog _log;
    private readonly SessionSnapshotStore _store;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;
    private readonly AppSettings _settings = new();

    public ReadinessFreshnessTests()
    {
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _store = new SessionSnapshotStore(Path.Combine(_dir, "session.json"), _log);
        _perSessionStore = new PerSessionSnapshotStore(Path.Combine(_dir, "sessions"), _log);
        _recentStore = new RecentSessionsStore(Path.Combine(_dir, "recent-sessions.json"), _log);
    }

    [Fact]
    public void ProviderRefresh_SameSelectionWithinStableTtl_DoesNotQueue()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Initialize();
        using var vm = new EmbeddedPlaybackViewModel(coordinator);
        var snapshot = vm.CaptureProviderHealthSelectionSnapshot();
        var now = DateTimeOffset.UtcNow;

        vm.RecordProviderHealthRefreshForTests(snapshot, now.AddSeconds(-2));

        Assert.False(vm.ShouldQueueProviderHealthRefresh(snapshot, force: false, now));
    }

    [Fact]
    public void ProviderRefresh_SameSelectionAfterStableTtl_Queues()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Initialize();
        using var vm = new EmbeddedPlaybackViewModel(coordinator);
        var snapshot = vm.CaptureProviderHealthSelectionSnapshot();
        var now = DateTimeOffset.UtcNow;

        vm.RecordProviderHealthRefreshForTests(snapshot, now.AddSeconds(-9));

        Assert.True(vm.ShouldQueueProviderHealthRefresh(snapshot, force: false, now));
    }

    [Fact]
    public void ProviderRefresh_TransientStateUsesShorterTtl()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Initialize();
        using var vm = new EmbeddedPlaybackViewModel(coordinator);
        var snapshot = vm.CaptureProviderHealthSelectionSnapshot();
        var now = DateTimeOffset.UtcNow;

        vm.ProviderHealthSnapshots.Add(new ProviderHealthSnapshot(
            Section: "Translation",
            ProviderId: "test",
            SelectionLabel: "gpu/test/model",
            RuntimeLabel: "Gpu",
            StatusLine: "⏳ host warming",
            InlineStatus: "⏳ host warming",
            Detail: "warming",
            HostState: "Managed local GPU host checking",
            MetricsText: string.Empty,
            IsReady: false,
            IsLive: false,
            IsStale: false,
            CheckedAtText: "now",
            History: []));
        vm.RecordProviderHealthRefreshForTests(snapshot, now.AddSeconds(-2));
        Assert.False(vm.ShouldQueueProviderHealthRefresh(snapshot, force: false, now));

        vm.RecordProviderHealthRefreshForTests(snapshot, now.AddSeconds(-4));
        Assert.True(vm.ShouldQueueProviderHealthRefresh(snapshot, force: false, now));
    }

    [Fact]
    public async Task ProbeCompletion_EmitsCoordinatorReadinessSignal()
    {
        var probe = new ContainerizedServiceProbe(_log, (url, _, _) =>
            Task.FromResult(new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: true,
                CudaVersion: "12.8",
                ServiceUrl: url,
                ErrorMessage: null,
                Capabilities: new ContainerCapabilitiesSnapshot(true, null, true, null, true, null))));
        using var coordinator = CreateCoordinator(probe);
        coordinator.Initialize();

        var signalTcs = new TaskCompletionSource<ReadinessSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.ReadinessSignals.Subscribe(signal =>
        {
            if (signal.Kind == ReadinessSignalKind.ProbeResultUpdated)
                signalTcs.TrySetResult(signal);
        });

        _ = probe.GetCurrentOrStartBackgroundProbe(_settings.EffectiveGpuServiceUrl, forceRefresh: true);
        var signal = await signalTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(ReadinessSignalKind.ProbeResultUpdated, signal.Kind);
        Assert.True(signal.ForceRefresh);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        _log.Dispose();
    }

    private SessionWorkflowCoordinator CreateCoordinator(ContainerizedServiceProbe? probe = null)
    {
        // Use fake registries so the VM's background health probe does not call
        // CheckReadiness on real providers (which would spawn Python probes and hang in CI).
        var registries = new RegistryBundle(
            _perSessionStore,
            _recentStore,
            new FakeTranscriptionRegistry(),
            new FakeTranslationRegistry(),
            new FakeTtsRegistry());
        var coreServices = new CoordinatorCoreServices(_store, _log, _settings);
        var options = new CoordinatorOptions
        {
            ContainerizedProbe = probe,
        };
        return new SessionWorkflowCoordinator(coreServices, registries, options);
    }
}
