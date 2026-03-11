using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

#pragma warning disable CS0067

public sealed class MediaSessionSeamTests
{
    [Fact]
    public void PlaybackBackendCoordinator_ProjectsBackendClockStateAndTracksIntoMediaSession()
    {
        var backend = new FakePlaybackBackend();
        var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        using var backendCoordinator = new PlaybackBackendCoordinator(backend, mediaSessionCoordinator);

        backend.EmitState(new PlaybackBackendState
        {
            Path = "C:\\Media\\sample.mp4",
            HasVideo = true,
            HasAudio = true,
            VideoWidth = 1920,
            VideoHeight = 1080,
            VideoDisplayWidth = 1920,
            VideoDisplayHeight = 1080,
            ActiveHardwareDecoder = "d3d11va"
        });
        backend.EmitTracks(
        [
            new MediaTrackInfo { Id = 1, Kind = MediaTrackKind.Audio, IsSelected = true, Language = "ja", Title = "Japanese" },
            new MediaTrackInfo { Id = 2, Kind = MediaTrackKind.Subtitle, IsSelected = true, Language = "en", Title = "English" }
        ]);
        backend.EmitClock(new ClockSnapshot(
            TimeSpan.FromSeconds(42),
            TimeSpan.FromMinutes(10),
            1.25,
            false,
            true,
            DateTimeOffset.UtcNow));

        var snapshot = mediaSessionCoordinator.Snapshot;

        Assert.Equal("C:\\Media\\sample.mp4", snapshot.Source.Path);
        Assert.True(snapshot.Source.IsLoaded);
        Assert.Equal(TimeSpan.FromSeconds(42), snapshot.Timeline.Position);
        Assert.Equal(TimeSpan.FromMinutes(10), snapshot.Timeline.Duration);
        Assert.Equal(1.25, snapshot.Timeline.Rate);
        Assert.False(snapshot.Timeline.IsPaused);
        Assert.True(snapshot.Timeline.IsSeekable);
        Assert.Equal("d3d11va", snapshot.Timeline.ActiveHardwareDecoder);
        Assert.Equal(2, snapshot.Streams.ActiveSubtitleTrackId);
        Assert.Equal(1, snapshot.Streams.ActiveAudioTrackId);
        Assert.Equal(2, snapshot.Streams.Tracks.Count);
    }

    [Fact]
    public async Task FakePlaybackBackend_DrivesActiveSubtitlePresentationThroughMediaSession()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:00,000 --> 00:00:05,000
First sentence

2
00:00:03,000 --> 00:00:06,000
Second sentence
""");

            var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var controller = new SubtitleWorkflowController(
                new CredentialFacade(new FakeCredentialStore()),
                mediaSessionCoordinator: mediaSessionCoordinator,
                environmentVariableReader: _ => null);
            var backend = new FakePlaybackBackend();
            using var backendCoordinator = new PlaybackBackendCoordinator(backend, mediaSessionCoordinator);

            await controller.LoadMediaSubtitlesAsync(videoPath);
            backend.EmitState(new PlaybackBackendState
            {
                Path = videoPath,
                HasVideo = true,
                HasAudio = true
            });
            backend.EmitClock(new ClockSnapshot(
                TimeSpan.FromSeconds(4),
                TimeSpan.FromMinutes(5),
                1.0,
                false,
                true,
                DateTimeOffset.UtcNow));

            var presentation = controller.GetOverlayPresentation(SubtitleRenderMode.SourceOnly);

            Assert.True(presentation.IsVisible);
            Assert.Equal("Second sentence", presentation.PrimaryText);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class FakePlaybackBackend : IPlaybackBackend
    {
        private readonly FakePlaybackClock _clock = new();
        private PlaybackBackendState _state = new();
        private IReadOnlyList<MediaTrackInfo> _tracks = [];

        public event Action<PlaybackBackendState>? StateChanged;
        public event Action<IReadOnlyList<MediaTrackInfo>>? TracksChanged;
        public event Action? MediaOpened;
        public event Action? MediaEnded;
        public event Action<string>? MediaFailed;
        public event Action<RuntimeInstallProgress>? RuntimeInstallProgress;

        public IPlaybackClock Clock => _clock;
        public PlaybackBackendState State => _state;
        public IReadOnlyList<MediaTrackInfo> CurrentTracks => _tracks;
        public HardwareDecodingMode HardwareDecodingMode { get; private set; } = HardwareDecodingMode.AutoSafe;

        public Task InitializeAsync(nint hostHandle, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LoadAsync(string path, CancellationToken cancellationToken)
        {
            _state = _state with { Path = path };
            StateChanged?.Invoke(_state);
            MediaOpened?.Invoke();
            return Task.CompletedTask;
        }

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
        public Task SetHardwareDecodingModeAsync(HardwareDecodingMode mode, CancellationToken cancellationToken)
        {
            HardwareDecodingMode = mode;
            return Task.CompletedTask;
        }

        public Task SetZoomAsync(double zoom, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetPanAsync(double x, double y, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ScreenshotAsync(string outputPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void EmitState(PlaybackBackendState state)
        {
            _state = state;
            StateChanged?.Invoke(state);
        }

        public void EmitTracks(IReadOnlyList<MediaTrackInfo> tracks)
        {
            _tracks = tracks;
            TracksChanged?.Invoke(tracks);
        }

        public void EmitClock(ClockSnapshot snapshot)
        {
            _clock.Emit(snapshot);
        }

        private sealed class FakePlaybackClock : IPlaybackClock
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

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public string? GetOpenAiApiKey() => null;
        public void SaveOpenAiApiKey(string apiKey) { }
        public string? GetGoogleTranslateApiKey() => null;
        public void SaveGoogleTranslateApiKey(string apiKey) { }
        public string? GetDeepLApiKey() => null;
        public void SaveDeepLApiKey(string apiKey) { }
        public string? GetMicrosoftTranslatorApiKey() => null;
        public void SaveMicrosoftTranslatorApiKey(string apiKey) { }
        public string? GetMicrosoftTranslatorRegion() => null;
        public void SaveMicrosoftTranslatorRegion(string region) { }
        public string? GetSubtitleModelKey() => null;
        public void SaveSubtitleModelKey(string modelKey) { }
        public string? GetTranslationModelKey() => null;
        public void SaveTranslationModelKey(string modelKey) { }
        public void ClearTranslationModelKey() { }
        public bool GetAutoTranslateEnabled() => false;
        public void SaveAutoTranslateEnabled(bool enabled) { }
        public string? GetLlamaCppServerPath() => null;
        public void SaveLlamaCppServerPath(string path) { }
        public string? GetLlamaCppRuntimeVersion() => null;
        public void SaveLlamaCppRuntimeVersion(string version) { }
        public string? GetLlamaCppRuntimeSource() => null;
        public void SaveLlamaCppRuntimeSource(string source) { }
    }
}

#pragma warning restore CS0067
