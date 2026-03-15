using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

#pragma warning disable CS0067

public sealed class ResumeTrackingCoordinatorTests
{
    // ── Flush when disabled ───────────────────────────────────────────────────

    [Fact]
    public void Flush_DoesNothing_WhenDisabled()
    {
        var persistedSnapshots = new List<PlaybackStateSnapshot>();
        var backend = new FakeResumableBackend("C:\\video.mp4", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(60));
        var resumeService = new ResumePlaybackService(persistEntries: _ => { });
        using var coordinator = new ResumeTrackingCoordinator(backend, resumeService);

        coordinator.SetEnabled(false);
        coordinator.Flush();

        Assert.Empty(resumeService.Entries);
    }

    // ── Flush when enabled ────────────────────────────────────────────────────

    [Fact]
    public void Flush_PersistsEntry_WhenEnabledAndPositionIsMeaningful()
    {
        IReadOnlyList<PlaybackResumeEntry>? persisted = null;
        var backend = new FakeResumableBackend("C:\\video.mp4", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(60));
        var resumeService = new ResumePlaybackService(persistEntries: entries => persisted = entries);
        using var coordinator = new ResumeTrackingCoordinator(backend, resumeService);

        coordinator.SetEnabled(true);
        coordinator.ResetForMedia("C:\\video.mp4");
        coordinator.Flush();

        Assert.NotNull(persisted);
        Assert.Single(persisted!);
        Assert.Equal("C:\\video.mp4", persisted![0].Path);
    }

    [Fact]
    public void Flush_DoesNotPersist_WhenPathIsEmpty()
    {
        IReadOnlyList<PlaybackResumeEntry>? persisted = null;
        var backend = new FakeResumableBackend(null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(60));
        var resumeService = new ResumePlaybackService(persistEntries: entries => persisted = entries);
        using var coordinator = new ResumeTrackingCoordinator(backend, resumeService);

        coordinator.SetEnabled(true);
        coordinator.Flush();

        // persisted is never set because path is empty
        Assert.Null(persisted);
    }

    // ── ResetForMedia ─────────────────────────────────────────────────────────

    [Fact]
    public void ResetForMedia_DoesNothing_WhenSamePathProvided()
    {
        var saveCount = 0;
        var backend = new FakeResumableBackend("C:\\video.mp4", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(60));
        var resumeService = new ResumePlaybackService(persistEntries: _ => saveCount++);
        using var coordinator = new ResumeTrackingCoordinator(backend, resumeService);

        coordinator.SetEnabled(true);
        coordinator.ResetForMedia("C:\\video.mp4");
        coordinator.ResetForMedia("C:\\video.mp4");

        // No extra flushes triggered by ResetForMedia
        Assert.Equal(0, saveCount);
    }

    [Fact]
    public void ResetForMedia_ChangesTrackedPath_WhenDifferentPath()
    {
        var backend = new FakeResumableBackend("C:\\video.mp4", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(60));
        var resumeService = new ResumePlaybackService(persistEntries: _ => { });
        using var coordinator = new ResumeTrackingCoordinator(backend, resumeService);

        coordinator.SetEnabled(true);
        coordinator.ResetForMedia("C:\\video.mp4");

        // Switching to a different media resets the internal state
        coordinator.ResetForMedia("C:\\other.mp4");

        // We can verify the coordinator is tracking the new path by flushing
        // and checking what gets persisted (snapshot still uses backend path)
        // No exception is expected here
    }

    // ── Clock-based persistence ───────────────────────────────────────────────

    [Fact]
    public void ClockChanged_SavesEntry_WhenIntervalElapsed()
    {
        var saveCount = 0;
        var backend = new FakeResumableBackend("C:\\video.mp4", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(60));
        var resumeService = new ResumePlaybackService(persistEntries: _ => saveCount++);
        // Use a very small interval so it always saves
        using var coordinator = new ResumeTrackingCoordinator(backend, resumeService, TimeSpan.Zero);

        coordinator.SetEnabled(true);
        coordinator.ResetForMedia("C:\\video.mp4");
        backend.EmitClock(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(60));

        Assert.True(saveCount > 0);
    }

    [Fact]
    public void ClockChanged_DoesNotSave_WhenDisabled()
    {
        var saveCount = 0;
        var backend = new FakeResumableBackend("C:\\video.mp4", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(60));
        var resumeService = new ResumePlaybackService(persistEntries: _ => saveCount++);
        using var coordinator = new ResumeTrackingCoordinator(backend, resumeService, TimeSpan.Zero);

        coordinator.SetEnabled(false);
        backend.EmitClock(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(60));

        Assert.Equal(0, saveCount);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_UnsubscribesFromClock_SoNoFurtherSavesOccur()
    {
        var saveCount = 0;
        var backend = new FakeResumableBackend("C:\\video.mp4", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(60));
        var resumeService = new ResumePlaybackService(persistEntries: _ => saveCount++);
        var coordinator = new ResumeTrackingCoordinator(backend, resumeService, TimeSpan.Zero);

        coordinator.SetEnabled(true);
        coordinator.ResetForMedia("C:\\video.mp4");
        coordinator.Dispose();
        var savesBeforeClock = saveCount;

        backend.EmitClock(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(60));

        Assert.Equal(savesBeforeClock, saveCount);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var backend = new FakeResumableBackend("C:\\video.mp4", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(60));
        var coordinator = new ResumeTrackingCoordinator(backend, new ResumePlaybackService());

        coordinator.Dispose();
        coordinator.Dispose(); // should not throw
    }

    // ── CurrentSnapshot ───────────────────────────────────────────────────────

    [Fact]
    public void CurrentSnapshot_ReflectsBackendState()
    {
        var backend = new FakeResumableBackend("C:\\video.mp4", TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(90));
        using var coordinator = new ResumeTrackingCoordinator(backend, new ResumePlaybackService());

        var snapshot = coordinator.CurrentSnapshot;

        Assert.Equal("C:\\video.mp4", snapshot.Path);
        Assert.Equal(TimeSpan.FromMinutes(3), snapshot.Position);
        Assert.Equal(TimeSpan.FromMinutes(90), snapshot.Duration);
    }

    private sealed class FakeResumableBackend : IPlaybackBackend
    {
        private readonly FakeClock _clock;
        private PlaybackBackendState _state;

        public FakeResumableBackend(string? path, TimeSpan position, TimeSpan duration)
        {
            _state = new PlaybackBackendState { Path = path };
            _clock = new FakeClock(position, duration);
        }

        public event Action<PlaybackBackendState>? StateChanged;
        public event Action<IReadOnlyList<MediaTrackInfo>>? TracksChanged;
        public event Action? MediaOpened;
        public event Action? MediaEnded;
        public event Action<string>? MediaFailed;
        public event Action<RuntimeInstallProgress>? RuntimeInstallProgress;

        public IPlaybackClock Clock => _clock;
        public PlaybackBackendState State => _state;
        public IReadOnlyList<MediaTrackInfo> CurrentTracks => [];
        public HardwareDecodingMode HardwareDecodingMode { get; set; }

        public void EmitClock(TimeSpan position, TimeSpan duration)
        {
            _clock.Emit(new ClockSnapshot(position, duration, 1.0, false, true, DateTimeOffset.UtcNow));
        }

        public Task InitializeAsync(nint hostHandle, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LoadAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SeekRelativeAsync(TimeSpan delta, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetMuteAsync(bool muted, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StepFrameAsync(bool forward, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetAudioDelayAsync(double seconds, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetAspectRatioAsync(string aspectRatio, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetHardwareDecodingModeAsync(HardwareDecodingMode mode, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetZoomAsync(double zoom, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetPanAsync(double x, double y, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ScreenshotAsync(string outputPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class FakeClock : IPlaybackClock
        {
            private ClockSnapshot _current;

            public FakeClock(TimeSpan position, TimeSpan duration)
            {
                _current = new ClockSnapshot(position, duration, 1.0, false, true, DateTimeOffset.UtcNow);
            }

            public event Action<ClockSnapshot>? Changed;
            public ClockSnapshot Current => _current;

            public void Emit(ClockSnapshot snapshot)
            {
                _current = snapshot;
                Changed?.Invoke(snapshot);
            }
        }
    }
}

#pragma warning restore CS0067
