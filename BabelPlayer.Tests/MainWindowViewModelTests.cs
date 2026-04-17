using System;
using System.IO;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;

namespace BabelPlayer.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-main-window-vm-tests-{Guid.NewGuid():N}");
    private readonly AppLog _log;
    private readonly SessionSnapshotStore _store;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;
    private readonly SettingsService _settingsService;

    public MainWindowViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _store = new SessionSnapshotStore(Path.Combine(_dir, "session.json"), _log);
        _perSessionStore = new PerSessionSnapshotStore(Path.Combine(_dir, "sessions"), _log);
        _recentStore = new RecentSessionsStore(Path.Combine(_dir, "recent-sessions.json"), _log);
        _settingsService = new SettingsService(Path.Combine(_dir, "app-settings.json"), _log);
    }

    [Fact]
    public async Task TryShowManagedBackendWarmupNoticeAsync_PersistsOptOutWhenDialogReturnsTrue()
    {
        var settings = new AppSettings
        {
            AlwaysStartLocalGpuRuntimeAtAppStart = true,
        };

        using var coordinator = CreateCoordinator(settings);
        using var viewModel = new MainWindowViewModel(
            coordinator,
            _settingsService,
            new ModelDownloader(_log),
            dialogService: new FakeDialogService(true));

        await viewModel.TryShowManagedBackendWarmupNoticeAsync();

        Assert.True(coordinator.CurrentSettings.ShownManagedBackendWarmupNotice);
        Assert.True(_settingsService.LoadOrDefault().ShownManagedBackendWarmupNotice);
    }

    [Fact]
    public async Task TryShowManagedBackendWarmupNoticeAsync_SkipsDialogWhenStartupWarmupIsDisabled()
    {
        var settings = new AppSettings
        {
            AlwaysStartLocalGpuRuntimeAtAppStart = false,
        };

        using var coordinator = CreateCoordinator(settings);
        var dialogService = new FakeDialogService(true);
        using var viewModel = new MainWindowViewModel(
            coordinator,
            _settingsService,
            new ModelDownloader(_log),
            dialogService: dialogService);

        await viewModel.TryShowManagedBackendWarmupNoticeAsync();

        Assert.Equal(0, dialogService.CallCount);
        Assert.False(coordinator.CurrentSettings.ShownManagedBackendWarmupNotice);
    }

    public void Dispose()
    {
        _log.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private SessionWorkflowCoordinator CreateCoordinator(AppSettings settings)
    {
        var registries = new RegistryBundle(
            _perSessionStore,
            _recentStore,
            new TranscriptionRegistry(_log),
            new TranslationRegistry(_log),
            new TtsRegistry(_log));
        var coreServices = new CoordinatorCoreServices(_store, _log, settings);
        return new SessionWorkflowCoordinator(coreServices, registries);
    }

    private sealed class FakeDialogService(bool result) : IDialogService
    {
        public int CallCount { get; private set; }

        public Task<bool> ShowWarmupNoticeAsync()
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
