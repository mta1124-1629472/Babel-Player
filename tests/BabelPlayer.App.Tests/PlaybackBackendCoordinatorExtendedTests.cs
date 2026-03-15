using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

#pragma warning disable CS0067

public sealed class PlaybackBackendCoordinatorExtendedTests
{
    [Fact]
    public void PlaybackBackendCoordinator_StateChange_UpdatesSessionSnapshot()
    {
        var backend = new FakeBackend();
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        using var backendCoordinator = new PlaybackBackendCoordinator(backend, coordinator);

        backend.EmitState(new PlaybackBackendState
        {
            Path = "C:\\media\\film.mkv",
            HasVideo = true,
            HasAudio = true,
            VideoWidth = 1280,
            VideoHeight = 720
        });

        var snapshot = coordinator.Snapshot;
        Assert.Equal("C:\\media\\film.mkv", snapshot.Source.Path);
        Assert.True(snapshot.Timeline.HasVideo);
        Assert.Equal(1280, snapshot.Timeline.VideoWidth);
    }

    [Fact]
    public void PlaybackBackendCoordinator_TracksChange_UpdatesSessionStreams()
    {
        var backend = new FakeBackend();
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        using var backendCoordinator = new PlaybackBackendCoordinator(backend, coordinator);

        backend.EmitTracks(
        [
            new MediaTrackInfo { Id = 10, Kind = MediaTrackKind.Audio, Language = "en", IsSelected = true },
            new MediaTrackInfo { Id = 20, Kind = MediaTrackKind.Subtitle, Language = "fr", IsSelected = false }
        ]);

        var streams = coordinator.Snapshot.Streams;
        Assert.Equal(2, streams.Tracks.Count);
        Assert.Equal(10, streams.ActiveAudioTrackId);
        Assert.Null(streams.ActiveSubtitleTrackId);
    }

    [Fact]
    public void PlaybackBackendCoordinator_ClockChange_UpdatesSessionTimeline()
    {
        var backend = new FakeBackend();
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        using var backendCoordinator = new PlaybackBackendCoordinator(backend, coordinator);

        backend.EmitClock(new ClockSnapshot(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(90),
            2.0,
            true,
            false,
            DateTimeOffset.UtcNow));

        var timeline = coordinator.Snapshot.Timeline;
        Assert.Equal(TimeSpan.FromSeconds(30), timeline.Position);
        Assert.Equal(TimeSpan.FromMinutes(90), timeline.Duration);
        Assert.Equal(2.0, timeline.Rate);
        Assert.True(timeline.IsPaused);
        Assert.False(timeline.IsSeekable);
    }

    [Fact]
    public void PlaybackBackendCoordinator_Dispose_StopsForwardingEvents()
    {
        var backend = new FakeBackend();
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var backendCoordinator = new PlaybackBackendCoordinator(backend, coordinator);

        backendCoordinator.Dispose();

        backend.EmitState(new PlaybackBackendState { Path = "C:\\new.mp4" });
        backend.EmitClock(new ClockSnapshot(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), 1.0, false, true, DateTimeOffset.UtcNow));

        // After dispose, the path from the emit above should NOT be in the snapshot
        // (snapshot stays as it was before dispose)
        Assert.Null(coordinator.Snapshot.Source.Path);
    }

    [Fact]
    public void PlaybackBackendCoordinator_Dispose_IsIdempotent()
    {
        var backend = new FakeBackend();
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var backendCoordinator = new PlaybackBackendCoordinator(backend, coordinator);

        backendCoordinator.Dispose();
        backendCoordinator.Dispose(); // should not throw
    }

    [Fact]
    public void PlaybackBackendCoordinator_InitialState_IsProjectedOnConstruction()
    {
        var backend = new FakeBackend();
        backend.PrepareState(new PlaybackBackendState { Path = "C:\\initial.mp4", HasVideo = true });

        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        using var _ = new PlaybackBackendCoordinator(backend, coordinator);

        Assert.Equal("C:\\initial.mp4", coordinator.Snapshot.Source.Path);
        Assert.True(coordinator.Snapshot.Timeline.HasVideo);
    }

    private sealed class FakeBackend : IPlaybackBackend
    {
        private readonly FakeBackendClock _clock = new();
        private PlaybackBackendState _state = new();

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

        public void PrepareState(PlaybackBackendState state) => _state = state;

        public void EmitState(PlaybackBackendState state)
        {
            _state = state;
            StateChanged?.Invoke(state);
        }

        public void EmitTracks(IReadOnlyList<MediaTrackInfo> tracks) => TracksChanged?.Invoke(tracks);

        public void EmitClock(ClockSnapshot snapshot) => _clock.Emit(snapshot);

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

        private sealed class FakeBackendClock : IPlaybackClock
        {
            private ClockSnapshot _current = new(TimeSpan.Zero, TimeSpan.Zero, 1.0, true, false, DateTimeOffset.UtcNow);
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
