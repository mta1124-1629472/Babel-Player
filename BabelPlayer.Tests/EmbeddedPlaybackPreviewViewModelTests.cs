using System;
using System.IO;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;

namespace BabelPlayer.Tests;

[Xunit.Trait("Category", "Quarantined")]
public sealed class EmbeddedPlaybackPreviewViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-preview-vm-tests-{Guid.NewGuid():N}");
    private readonly AppLog _log;
    private readonly SessionSnapshotStore _store;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;

    public EmbeddedPlaybackPreviewViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _store = new SessionSnapshotStore(Path.Combine(_dir, "session.json"), _log);
        _perSessionStore = new PerSessionSnapshotStore(Path.Combine(_dir, "sessions"), _log);
        _recentStore = new RecentSessionsStore(Path.Combine(_dir, "recent-sessions.json"), _log);
    }

    [Fact]
    public void SyncPaneLayoutFromSettings_ProjectsRolesOntoPhysicalSides()
    {
        var settings = new AppSettings
        {
            IsPipelinePaneVisible = true,
            IsSegmentsPaneVisible = true,
            PipelinePaneWidth = 260,
            SegmentsPaneWidth = 340,
            SwapPaneSides = false
        };

        using var coordinator = CreateCoordinator(settings);
        using var playback = new EmbeddedPlaybackViewModel(coordinator);
        var preview = playback.Preview;

        Assert.True(preview.IsPipelinePaneOnLeft);
        Assert.Equal(260, preview.LeftPaneWidth, precision: 3);
        Assert.Equal(340, preview.RightPaneWidth, precision: 3);

        coordinator.CurrentSettings.SwapPaneSides = true;
        preview.SyncPaneLayoutFromSettings();

        Assert.False(preview.IsPipelinePaneOnLeft);
        Assert.Equal(340, preview.LeftPaneWidth, precision: 3);
        Assert.Equal(260, preview.RightPaneWidth, precision: 3);
        Assert.Equal(4, preview.PipelinePaneColumn);
        Assert.Equal(0, preview.SegmentsPaneColumn);
    }

    [Fact]
    public void Fullscreen_HidesPhysicalSideProjection_WithoutClearingRoleVisibility()
    {
        var settings = new AppSettings
        {
            IsPipelinePaneVisible = true,
            IsSegmentsPaneVisible = true
        };

        using var coordinator = CreateCoordinator(settings);
        using var playback = new EmbeddedPlaybackViewModel(coordinator);
        var preview = playback.Preview;

        Assert.True(preview.IsLeftPaneVisible);
        Assert.True(preview.IsRightPaneVisible);

        preview.IsFullscreen = true;

        Assert.True(preview.IsPipelinePaneVisible);
        Assert.True(preview.IsSegmentsPaneVisible);
        Assert.False(preview.IsLeftPaneVisible);
        Assert.False(preview.IsRightPaneVisible);

        preview.IsFullscreen = false;

        Assert.True(preview.IsLeftPaneVisible);
        Assert.True(preview.IsRightPaneVisible);
    }

    [Fact]
    public void PhysicalSideCommands_TargetRoleAssignedToThatSide()
    {
        var settings = new AppSettings
        {
            IsPipelinePaneVisible = true,
            IsSegmentsPaneVisible = true,
            PipelinePaneWidth = 320,
            SegmentsPaneWidth = 470,
            SwapPaneSides = true
        };

        using var coordinator = CreateCoordinator(settings);
        using var playback = new EmbeddedPlaybackViewModel(coordinator);
        var preview = playback.Preview;

        preview.ToggleLeftPaneCommand.Execute(null);

        Assert.True(preview.IsPipelinePaneVisible);
        Assert.False(preview.IsSegmentsPaneVisible);
        Assert.False(coordinator.CurrentSettings.IsSegmentsPaneVisible);

        preview.ResetRightPaneWidthCommand.Execute(null);

        Assert.Equal(AppSettings.PipelinePaneDefaultWidth, preview.PipelinePaneWidth, precision: 3);
        Assert.Equal(AppSettings.PipelinePaneDefaultWidth, coordinator.CurrentSettings.PipelinePaneWidth, precision: 3);
        Assert.Equal(470, preview.SegmentsPaneWidth, precision: 3);
    }

    public void Dispose()
    {
        _log.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException ex)
        {
            throw new IOException($"Failed to clean preview VM test directory '{_dir}'.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"Failed to clean preview VM test directory '{_dir}'.", ex);
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
}
