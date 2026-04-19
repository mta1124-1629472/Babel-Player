using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;

namespace BabelPlayer.Tests;

[Trait("Category", "Quarantined")]
public sealed class EmbeddedPlaybackPreviewDubPreviewTests
{
    [Fact]
    public async Task DubPreview_WithAmbianceStem_LoadsAndPlaysAmbiancePlayer()
    {
        using var harness = new PreviewHarness(includeAmbiance: true);

        await harness.Playback.Preview.SelectSegmentAndSeekAsync(harness.Segment, playSource: true);
        harness.Playback.Preview.IsDubModeOn = true;

        await harness.SegmentPlayer.WaitForLoadCountAsync();
        await harness.AmbiancePlayer.WaitForPlayCountAsync();

        Assert.Equal(harness.AmbiancePath, harness.AmbiancePlayer.LastLoadedFile);
        Assert.True(harness.AmbiancePlayer.PlayCallCount > 0);
        Assert.Equal(harness.SegmentAudioPath, harness.SegmentPlayer.LastLoadedFile);
        Assert.Equal(0.0, harness.SourcePlayer.Volume);
        Assert.Contains("separated ambience", harness.Playback.Preview.DubMixControlTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Dub preview uses separated ambience.", harness.Playback.StatusText);
    }

    [Fact]
    public async Task DubPreview_WithAmbianceStem_SeekPausePlaySyncsAmbiancePlayer()
    {
        using var harness = new PreviewHarness(includeAmbiance: true);

        await harness.Playback.Preview.SelectSegmentAndSeekAsync(harness.Segment, playSource: true);
        harness.Playback.Preview.IsDubModeOn = true;
        await harness.AmbiancePlayer.WaitForPlayCountAsync();

        await harness.Playback.Preview.PlayPauseSourceCommand.ExecuteAsync(null);
        Assert.True(harness.AmbiancePlayer.PauseCallCount > 0);

        harness.Playback.Preview.SourcePositionMs = 1500;
        Assert.Equal(1500L, harness.AmbiancePlayer.CurrentTime);

        var playCallCountAfterInitialStart = harness.AmbiancePlayer.PlayCallCount;
        await harness.Playback.Preview.PlayPauseSourceCommand.ExecuteAsync(null);
        await harness.AmbiancePlayer.WaitForPlayCountAsync(playCallCountAfterInitialStart + 1);

        Assert.True(harness.AmbiancePlayer.PlayCallCount > playCallCountAfterInitialStart);
    }

    [Fact]
    public async Task DubPreview_WithAmbianceStem_DubMixControlUpdatesAmbianceVolume()
    {
        using var harness = new PreviewHarness(includeAmbiance: true);

        await harness.Playback.Preview.SelectSegmentAndSeekAsync(harness.Segment, playSource: true);
        harness.Playback.Preview.IsDubModeOn = true;
        await harness.AmbiancePlayer.WaitForPlayCountAsync();

        harness.Playback.Preview.DubMixControlDb = -6.0;

        var expectedGain = Math.Pow(10.0, -6.0 / 20.0);
        Assert.Equal(-6.0, harness.Coordinator.CurrentSettings.AmbianceMixDb, precision: 3);
        Assert.InRange(harness.AmbiancePlayer.Volume, expectedGain - 0.0001, expectedGain + 0.0001);
    }

    [Fact]
    public async Task DubPreview_WithoutAmbianceStem_UsesDuckedSourceFallback()
    {
        using var harness = new PreviewHarness(includeAmbiance: false);

        await harness.Playback.Preview.SelectSegmentAndSeekAsync(harness.Segment, playSource: true);
        harness.Playback.Preview.IsDubModeOn = true;
        await harness.SegmentPlayer.WaitForLoadCountAsync();

        var expectedGain = Math.Pow(10.0, -15.0 / 20.0);
        Assert.Null(harness.AmbiancePlayer.LastLoadedFile);
        Assert.InRange(harness.SourcePlayer.Volume, expectedGain - 0.0001, expectedGain + 0.0001);
        Assert.Equal("Duck", harness.Playback.Preview.DubMixControlLabel);
        Assert.Contains("Approximate", harness.Playback.Preview.DubMixControlTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Dub preview uses source audio fallback.", harness.Playback.StatusText);
    }

    private sealed class PreviewHarness : IDisposable
    {
        private readonly string _dir;
        private readonly AppLog _log;

        public PreviewHarness(bool includeAmbiance)
        {
            _dir = Path.Combine(Path.GetTempPath(), $"babel-preview-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);

            _log = new AppLog(Path.Combine(_dir, "preview.log"));
            var sessionStore = new SessionSnapshotStore(Path.Combine(_dir, "session.json"), _log);
            var perSessionStore = new PerSessionSnapshotStore(Path.Combine(_dir, "sessions"), _log);
            var recentStore = new RecentSessionsStore(Path.Combine(_dir, "recent-sessions.json"), _log);
            var settings = new AppSettings
            {
                TranscriptionProvider = "fake-transcription",
                TranslationProvider = "fake-translation",
                TtsProvider = "fake-tts",
                TtsVoice = "default",
                TargetLanguage = "en",
            };

            SourcePlayer = new FakeMediaTransport();
            SegmentPlayer = new FakeMediaTransport();
            AmbiancePlayer = new FakeMediaTransport();

            var coreServices = new CoordinatorCoreServices(sessionStore, _log, settings);
            var registries = new RegistryBundle(
                perSessionStore,
                recentStore,
                new FakeTranscriptionRegistry(),
                new FakeTranslationRegistry(),
                new FakeTtsRegistry());
            var transportManager = new MediaTransportManager(
                segmentPlayer: SegmentPlayer,
                sourcePlayer: SourcePlayer,
                ambiancePlayer: AmbiancePlayer);

            Coordinator = new SessionWorkflowCoordinator(
                coreServices,
                transportManager,
                registries,
                new CoordinatorOptions
                {
                    AudioProcessingService = new FakeAudioProcessingService(),
                });

            SourceMediaPath = CreateFile("source.mp4");
            IngestedMediaPath = CreateFile("ingested.mp4");
            SegmentAudioPath = CreateFile("segment_0.0.mp3");
            AmbiancePath = includeAmbiance ? CreateFile("ambiance.wav") : null;

            var now = DateTimeOffset.UtcNow;
            Coordinator.CurrentSession = WorkflowSessionSnapshot.CreateNew(now) with
            {
                Stage = SessionWorkflowStage.TtsGenerated,
                SourceMediaPath = SourceMediaPath,
                IngestedMediaPath = IngestedMediaPath,
                MediaLoadedAtUtc = now,
                AmbianceAudioPath = AmbiancePath,
                TtsGeneratedAtUtc = now,
                TtsSegmentAudioPaths = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["segment_0.0"] = SegmentAudioPath,
                },
            };

            Playback = new EmbeddedPlaybackViewModel(Coordinator);
            Segment = new WorkflowSegmentState(
                "segment_0.0",
                0.0,
                2.0,
                "Hello world.",
                true,
                "Hola mundo.",
                true);
            Playback.Preview.Segments = new ObservableCollection<WorkflowSegmentState>([Segment]);
            Playback.Preview.HasSegments = true;
            Playback.Preview.SyncDubMixControlFromSettings();
        }

        public SessionWorkflowCoordinator Coordinator { get; }
        public EmbeddedPlaybackViewModel Playback { get; }
        public FakeMediaTransport SourcePlayer { get; }
        public FakeMediaTransport SegmentPlayer { get; }
        public FakeMediaTransport AmbiancePlayer { get; }
        public WorkflowSegmentState Segment { get; }
        public string SourceMediaPath { get; }
        public string IngestedMediaPath { get; }
        public string SegmentAudioPath { get; }
        public string? AmbiancePath { get; }

        public void Dispose()
        {
            Playback.Dispose();
            Coordinator.Dispose();
            SourcePlayer.Dispose();
            SegmentPlayer.Dispose();
            AmbiancePlayer.Dispose();
            _log.Dispose();

            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup only.
            }
        }

        private string CreateFile(string fileName)
        {
            var path = Path.Combine(_dir, fileName);
            File.WriteAllBytes(path, [0x01, 0x02, 0x03]);
            return path;
        }
    }

    private sealed class FakeMediaTransport : IMediaTransport
    {
        private readonly object _sync = new();
        private TaskCompletionSource<int> _loadObserved = CreateSignal();
        private TaskCompletionSource<int> _playObserved = CreateSignal();
        public string? LastLoadedFile { get; private set; }
        public int PlayCallCount { get; private set; }
        public int PauseCallCount { get; private set; }
        public int LoadCallCount { get; private set; }
        public List<long> SeekHistory { get; } = [];

        public long CurrentTime { get; private set; }
        public long Duration { get; private set; } = 20_000;
        public bool HasEnded { get; private set; }
        public bool IsPlaying { get; private set; }
        public double Volume { get; set; } = 1.0;
        public double PlaybackRate { get; set; } = 1.0;
        public bool SubtitlesVisible { get; set; }

#pragma warning disable CS0067
        public event EventHandler? Ended;
        public event EventHandler<Exception>? ErrorOccurred;
#pragma warning restore CS0067

        public void Dispose()
        {
        }

        public void Load(string filePath)
        {
            TaskCompletionSource<int> signal;
            lock (_sync)
            {
                LastLoadedFile = filePath;
                LoadCallCount++;
                CurrentTime = 0;
                HasEnded = false;
                signal = _loadObserved;
                _loadObserved = CreateSignal();
            }

            signal.TrySetResult(LoadCallCount);
        }

        public void Play()
        {
            TaskCompletionSource<int> signal;
            lock (_sync)
            {
                PlayCallCount++;
                IsPlaying = true;
                HasEnded = false;
                signal = _playObserved;
                _playObserved = CreateSignal();
            }

            signal.TrySetResult(PlayCallCount);
        }

        public void Pause()
        {
            lock (_sync)
            {
                PauseCallCount++;
                IsPlaying = false;
            }
        }

        public void Seek(long positionMs)
        {
            CurrentTime = positionMs;
            SeekHistory.Add(positionMs);
        }

        public void LoadSubtitleTrack(string srtPath)
        {
        }

        public void RemoveAllSubtitleTracks()
        {
        }

        public Task WaitForLoadCountAsync(int expectedCount = 1) =>
            WaitForCountAsync(expectedCount, () => LoadCallCount, () => _loadObserved);

        public Task WaitForPlayCountAsync(int expectedCount = 1) =>
            WaitForCountAsync(expectedCount, () => PlayCallCount, () => _playObserved);

        private async Task WaitForCountAsync(
            int expectedCount,
            Func<int> countSelector,
            Func<TaskCompletionSource<int>> signalSelector,
            TimeSpan? timeout = null)
        {
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
            var stopwatch = Stopwatch.StartNew();

            while (true)
            {
                if (stopwatch.Elapsed >= effectiveTimeout)
                {
                    throw new TimeoutException(
                        $"Timed out waiting for count {expectedCount}; current count was {countSelector()} after {effectiveTimeout}.");
                }

                TaskCompletionSource<int> signal;
                lock (_sync)
                {
                    if (countSelector() >= expectedCount)
                        return;

                    signal = signalSelector();
                }

                int observedCount;
                try
                {
                    observedCount = await signal.Task.WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (TimeoutException) when (stopwatch.Elapsed < effectiveTimeout)
                {
                    continue;
                }

                if (observedCount >= expectedCount)
                    return;

                if (stopwatch.Elapsed >= effectiveTimeout)
                {
                    throw new TimeoutException(
                        $"Timed out waiting for count {expectedCount}; observed count was {observedCount} after {effectiveTimeout}.");
                }
            }
        }

        private static TaskCompletionSource<int> CreateSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
