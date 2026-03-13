using BabelPlayer.App;
using BabelPlayer.Core;
using BabelPlayer.Infrastructure;
using HardwareDecodingMode = BabelPlayer.App.ShellHardwareDecodingMode;
using PlaybackStateSnapshot = BabelPlayer.App.ShellPlaybackStateSnapshot;
using PlaybackWindowMode = BabelPlayer.App.ShellPlaybackWindowMode;
using ShortcutProfile = BabelPlayer.App.ShellShortcutProfile;
using SubtitleRenderMode = BabelPlayer.App.ShellSubtitleRenderMode;
using SubtitleStyleSettings = BabelPlayer.App.ShellSubtitleStyle;

namespace BabelPlayer.App.Tests;

public sealed class RemainingArchitecturePlanTests
{
    [Fact]
    public void ShellProjectionService_ProjectsImmutableTransportSubtitleAndTrackState()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        using var projectionService = new ShellProjectionService(coordinator.Store);

        coordinator.ApplyPlaybackState(new PlaybackBackendState
        {
            Path = "C:\\Media\\sample.mp4",
            HasVideo = true,
            HasAudio = true,
            VideoWidth = 1080,
            VideoHeight = 1920,
            VideoDisplayWidth = 1080,
            VideoDisplayHeight = 1920,
            Volume = 0.42,
            ActiveHardwareDecoder = "d3d11va"
        });
        coordinator.ApplyTracks(
        [
            new MediaTrackInfo { Id = 1, Kind = MediaTrackKind.Audio, Title = "Japanese", Language = "ja", IsSelected = true },
            new MediaTrackInfo { Id = 3, Kind = MediaTrackKind.Subtitle, Title = "English", Language = "en", IsSelected = true }
        ]);
        var transcript = new TranscriptSegment
        {
            Id = new TranscriptSegmentId("tr:1"),
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "Hola",
            Language = "es"
        };
        coordinator.SetTranscriptSegments([transcript], SubtitlePipelineSource.Sidecar, "es");
        coordinator.SetTranslationState(enabled: true, autoTranslateEnabled: true);
        coordinator.UpsertTranslationSegment(new TranslationSegment
        {
            Id = new TranslationSegmentId("tl:1"),
            SourceSegmentId = transcript.Id,
            Start = transcript.Start,
            End = transcript.End,
            Text = "Hello",
            Language = "en"
        });
        coordinator.ApplyClock(new ClockSnapshot(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(5),
            1.25,
            false,
            true,
            DateTimeOffset.UtcNow));

        var projection = projectionService.Current;

        Assert.Equal("C:\\Media\\sample.mp4", projection.Transport.Path);
        Assert.Equal(1.0, projection.Transport.PositionSeconds);
        Assert.Equal(300.0, projection.Transport.DurationSeconds);
        Assert.Equal("00:01", projection.Transport.CurrentTimeText);
        Assert.Equal("05:00", projection.Transport.DurationText);
        Assert.False(projection.Transport.IsPaused);
        Assert.True(projection.Transport.HasVideo);
        Assert.True(projection.Transport.HasAudio);
        Assert.True(projection.Transport.IsSeekable);
        Assert.Equal(1080, projection.Transport.VideoWidth);
        Assert.Equal(1920, projection.Transport.VideoHeight);
        Assert.Equal(1080, projection.Transport.VideoDisplayWidth);
        Assert.Equal(1920, projection.Transport.VideoDisplayHeight);
        Assert.Equal(0.42, projection.Transport.Volume);
        Assert.Equal("d3d11va", projection.Transport.ActiveHardwareDecoder);
        Assert.Equal(2, projection.SelectedTracks.Tracks.Count);
        Assert.Equal(1, projection.SelectedTracks.ActiveAudioTrackId);
        Assert.Equal(3, projection.SelectedTracks.ActiveSubtitleTrackId);
        Assert.Equal("Hola", projection.Subtitle.SourceText);
        Assert.Equal("Hello", projection.Subtitle.TranslationText);
        Assert.True(projection.Subtitle.IsTranslationEnabled);
        Assert.True(projection.Subtitle.IsAutoTranslateEnabled);

        ((ShellMediaTrack[])projection.SelectedTracks.Tracks)[0] = new ShellMediaTrack
        {
            Id = 1,
            Kind = ShellMediaTrackKind.Audio,
            Title = "mutated",
            Language = "ja",
            IsSelected = true
        };

        Assert.Equal("Japanese", coordinator.Snapshot.Streams.Tracks[0].Title);
    }

    [Fact]
    public void MediaSessionCoordinator_UpsertTranscriptSegment_ReplacesByIdAndMaintainsOrdering()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());

        coordinator.SetTranscriptSegments(
        [
            new TranscriptSegment
            {
                Id = new TranscriptSegmentId("tr:2"),
                Start = TimeSpan.FromSeconds(5),
                End = TimeSpan.FromSeconds(7),
                Text = "second",
                Language = "en"
            }
        ],
        SubtitlePipelineSource.Sidecar,
        "en");

        coordinator.UpsertTranscriptSegment(new TranscriptSegment
        {
            Id = new TranscriptSegmentId("tr:1"),
            Start = TimeSpan.FromSeconds(1),
            End = TimeSpan.FromSeconds(2),
            Text = "first",
            Language = "en"
        });

        coordinator.UpsertTranscriptSegment(new TranscriptSegment
        {
            Id = new TranscriptSegmentId("tr:2"),
            Start = TimeSpan.FromSeconds(5),
            End = TimeSpan.FromSeconds(8),
            Text = "second updated",
            Language = "en"
        });

        var segments = coordinator.Snapshot.Transcript.Segments;

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal("tr:1", first.Id.Value);
                Assert.Equal("first", first.Text);
            },
            second =>
            {
                Assert.Equal("tr:2", second.Id.Value);
                Assert.Equal("second updated", second.Text);
                Assert.Equal(TimeSpan.FromSeconds(8), second.End);
            });
    }

    [Fact]
    public async Task DefaultCaptionGenerator_FallsBackToNextAvailableProvider()
    {
        var calls = new List<string>();
        var registry = new TranscriptionProviderRegistry(
        [
            new FakeTranscriptionProvider("primary", shouldFail: true, calls),
            new FakeTranscriptionProvider("fallback", shouldFail: false, calls)
        ]);
        var generator = new DefaultCaptionGenerator(
            new ProviderAvailabilityContext(new CredentialFacade(new FakeCredentialStore()), _ => null),
            registry);

        var result = await generator.GenerateCaptionsAsync(
            "C:\\Media\\sample.mp4",
            SubtitleWorkflowCatalog.GetTranscriptionModel("local:tiny"),
            null,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(["primary", "fallback"], calls);
        Assert.Single(result);
        Assert.Equal("fallback", result[0].SourceText);
    }

    [Fact]
    public async Task ProviderBackedSubtitleTranslator_UsesRegisteredAdapter()
    {
        var adapter = new FakeTranslationProvider();
        var translator = new ProviderBackedSubtitleTranslator(
            new ProviderAvailabilityContext(new CredentialFacade(new FakeCredentialStore()), _ => null),
            new TranslationProviderRegistry([adapter]),
            new MtTranslationEngineFactory(),
            new FakeLocalModelRuntime());

        var translated = await translator.TranslateBatchAsync(
            SubtitleWorkflowCatalog.GetTranslationModel("cloud:deepl"),
            ["hola", "adios"],
            CancellationToken.None);

        Assert.Equal(1, adapter.CallCount);
        Assert.Equal(["HELLO", "BYE"], translated);
    }

    [Fact]
    public async Task SubtitleApplicationService_DelegatesToCollaborators()
    {
        var sourceResolver = new FakeSubtitleSourceResolver();
        var captionGenerator = new FakeCaptionGenerator();
        var subtitleTranslator = new FakeSubtitleTranslator();
        var credentialCoordinator = new FakeAiCredentialCoordinator();
        var runtimeProvisioner = new FakeRuntimeProvisioner();
        var credentialFacade = new CredentialFacade(new FakeCredentialStore());
        var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var workflowStateStore = new InMemorySubtitleWorkflowStateStore();
        var providerAvailabilityService = CreateProviderAvailabilityService(credentialFacade, _ => null);
        using var service = new SubtitleApplicationService(
            sourceResolver,
            captionGenerator,
            subtitleTranslator,
            credentialCoordinator,
            runtimeProvisioner,
            credentialFacade,
            mediaSessionCoordinator,
            workflowStateStore,
            providerAvailabilityService);

        await service.LoadExternalSubtitleCuesAsync("sub.srt", null, null, CancellationToken.None);
        await service.ExtractEmbeddedSubtitleCuesAsync("video.mp4", new MediaTrackInfo { Id = 9 }, null, null, CancellationToken.None);
        await service.GenerateCaptionsAsync("video.mp4", SubtitleWorkflowCatalog.GetTranscriptionModel("local:tiny"), null, null, null, CancellationToken.None);
        await service.TranslateAsync(SubtitleWorkflowCatalog.GetTranslationModel("cloud:deepl"), "hola", CancellationToken.None);
        await service.TranslateBatchAsync(SubtitleWorkflowCatalog.GetTranslationModel("cloud:deepl"), ["hola"], CancellationToken.None);
        await service.EnsureOpenAiApiKeyAsync(CancellationToken.None);
        await service.EnsureTranslationProviderCredentialsAsync(TranslationProvider.DeepL, CancellationToken.None);
        await service.EnsureLlamaCppAsync(null, CancellationToken.None);
        await service.EnsureFfmpegAsync(null, CancellationToken.None);

        Assert.Equal(1, sourceResolver.ExternalCalls);
        Assert.Equal(1, sourceResolver.EmbeddedCalls);
        Assert.Equal(1, captionGenerator.CallCount);
        Assert.Equal(2, subtitleTranslator.CallCount);
        Assert.Equal(1, credentialCoordinator.OpenAiCalls);
        Assert.Equal(1, credentialCoordinator.TranslationCalls);
        Assert.Equal(1, runtimeProvisioner.LlamaCalls);
        Assert.Equal(1, runtimeProvisioner.FfmpegCalls);
    }

    [Fact]
    public async Task SubtitleApplicationService_TriggersActiveSegmentTranslationFromMediaSessionChanges()
    {
        var credentialFacade = new CredentialFacade(new FakeCredentialStore());
        var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var workflowStateStore = new InMemorySubtitleWorkflowStateStore();
        var subtitleTranslator = new FakeSubtitleTranslator();
        using var service = new SubtitleApplicationService(
            new FakeSubtitleSourceResolver(),
            new FakeCaptionGenerator(),
            subtitleTranslator,
            new FakeAiCredentialCoordinator(),
            new FakeRuntimeProvisioner(),
            credentialFacade,
            mediaSessionCoordinator,
            workflowStateStore,
            CreateProviderAvailabilityService(credentialFacade, _ => null));

        workflowStateStore.Update(state => state with
        {
            SelectedTranslationModelKey = "cloud:deepl"
        });
        mediaSessionCoordinator.SetTranslationState(enabled: true, autoTranslateEnabled: false);
        mediaSessionCoordinator.SetTranscriptSegments(
        [
            new TranscriptSegment
            {
                Id = new TranscriptSegmentId("tr:active"),
                Start = TimeSpan.Zero,
                End = TimeSpan.FromSeconds(2),
                Text = "Hola",
                Language = "es"
            }
        ],
        SubtitlePipelineSource.Sidecar,
        "es");
        mediaSessionCoordinator.ApplyClock(new ClockSnapshot(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(5),
            1.0,
            false,
            true,
            DateTimeOffset.UtcNow));

        await WaitForConditionAsync(() => subtitleTranslator.CallCount == 1);

        Assert.Equal(1, subtitleTranslator.CallCount);
        Assert.Equal("Hola", mediaSessionCoordinator.Snapshot.Translation.Segments.Single().Text);
    }

    [Fact]
    public void SubtitleWorkflowStateStore_SnapshotsStayImmutableAcrossUpdates()
    {
        var store = new InMemorySubtitleWorkflowStateStore();
        store.Update(state => state with { CurrentVideoPath = "first.mp4" });
        var first = store.Snapshot;

        store.Update(state => state with { CurrentVideoPath = "second.mp4" });

        Assert.Equal("first.mp4", first.CurrentVideoPath);
        Assert.Equal("second.mp4", store.Snapshot.CurrentVideoPath);
    }

    [Fact]
    public void SubtitleWorkflowProjectionAdapter_RebuildsSnapshotFromWorkflowAndMediaSessionOnly()
    {
        var workflowStore = new InMemorySubtitleWorkflowStateStore();
        var mediaCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        using var adapter = new SubtitleWorkflowProjectionAdapter(workflowStore, mediaCoordinator.Store);

        workflowStore.Update(state => state with
        {
            CurrentVideoPath = "C:\\Media\\sample.mp4",
            SelectedTranscriptionModelKey = "local:small",
            SelectedTranslationModelKey = "cloud:deepl",
            CaptionGenerationModeLabel = "Local Small.en"
        });
        var transcript = new TranscriptSegment
        {
            Id = new TranscriptSegmentId("tr:1"),
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "Hola",
            Language = "es"
        };
        mediaCoordinator.SetTranscriptSegments([transcript], SubtitlePipelineSource.Sidecar, "es");
        mediaCoordinator.SetTranslationState(enabled: true, autoTranslateEnabled: true);
        mediaCoordinator.UpsertTranslationSegment(new TranslationSegment
        {
            Id = new TranslationSegmentId("tl:1"),
            SourceSegmentId = transcript.Id,
            Start = transcript.Start,
            End = transcript.End,
            Text = "Hello",
            Language = "en"
        });
        mediaCoordinator.ApplyClock(new ClockSnapshot(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(5),
            1.0,
            false,
            true,
            DateTimeOffset.UtcNow));

        var snapshot = adapter.Current;

        Assert.Equal("C:\\Media\\sample.mp4", snapshot.CurrentVideoPath);
        Assert.Equal("local:small-multilingual", snapshot.SelectedTranscriptionModelKey);
        Assert.Equal("cloud:deepl", snapshot.SelectedTranslationModelKey);
        Assert.True(snapshot.IsTranslationEnabled);
        Assert.True(snapshot.AutoTranslateEnabled);
        Assert.Equal("Hola", Assert.Single(snapshot.Cues).SourceText);
        Assert.Equal("Hello", snapshot.ActiveCue?.TranslatedText);
    }

    [Fact]
    public void ResumePlaybackService_BuildsFindsAndRemovesEntriesWithoutChangingShape()
    {
        IReadOnlyList<PlaybackResumeEntry>? persisted = null;
        var service = new ResumePlaybackService(
            initialEntries: [],
            persistEntries: entries => persisted = entries);
        var snapshot = new BabelPlayer.Core.PlaybackStateSnapshot
        {
            Path = "C:\\Media\\movie.mp4",
            Position = TimeSpan.FromMinutes(10),
            Duration = TimeSpan.FromMinutes(90)
        };

        var built = service.BuildEntry(snapshot);
        service.Update(snapshot);
        var found = service.FindEntry(snapshot.Path, snapshot.Duration);
        service.Update(snapshot with { Position = TimeSpan.FromMinutes(89) }, forceRemoveCompleted: true);

        Assert.NotNull(built);
        Assert.Equal("C:\\Media\\movie.mp4", built!.Path);
        Assert.Equal(600, built.PositionSeconds);
        Assert.Equal(5400, built.DurationSeconds);
        Assert.NotNull(found);
        Assert.Equal(built.Path, found!.Path);
        Assert.Empty(service.Entries);
        Assert.NotNull(persisted);
        Assert.Empty(persisted!);
    }

    [Fact]
    public async Task ResumeTrackingCoordinator_PersistsOnClockCadenceAndFlushesCompletedPlayback()
    {
        IReadOnlyList<PlaybackResumeEntry>? persisted = null;
        var backend = new FakeShellPlaybackBackend();
        var service = new ResumePlaybackService(
            initialEntries: [],
            persistEntries: entries => persisted = entries.Select(entry => new PlaybackResumeEntry
            {
                Path = entry.Path,
                PositionSeconds = entry.PositionSeconds,
                DurationSeconds = entry.DurationSeconds,
                UpdatedAt = entry.UpdatedAt
            }).ToArray());
        using var coordinator = new ResumeTrackingCoordinator(backend, service);
        coordinator.SetEnabled(true);
        await backend.LoadAsync("C:\\Media\\movie.mp4", CancellationToken.None);
        coordinator.ResetForMedia("C:\\Media\\movie.mp4");

        var start = DateTimeOffset.UtcNow;
        backend.ClockSnapshot = new ClockSnapshot(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(90), 1.0, false, true, start);
        Assert.NotNull(persisted);
        Assert.Single(persisted!);
        Assert.Equal(600, persisted![0].PositionSeconds);

        backend.ClockSnapshot = new ClockSnapshot(TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(90), 1.0, false, true, start.AddSeconds(3));
        Assert.Single(persisted!);
        Assert.Equal(600, persisted![0].PositionSeconds);

        backend.ClockSnapshot = new ClockSnapshot(TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(90), 1.0, false, true, start.AddSeconds(6));
        Assert.Single(persisted!);
        Assert.Equal(720, persisted![0].PositionSeconds);

        backend.ClockSnapshot = new ClockSnapshot(TimeSpan.FromMinutes(89), TimeSpan.FromMinutes(90), 1.0, false, true, start.AddSeconds(12));
        coordinator.Flush(forceRemoveCompleted: true);

        Assert.Empty(service.Entries);
    }

    [Fact]
    public async Task ResumeTrackingCoordinator_DoesNothingWhenDisabled()
    {
        var backend = new FakeShellPlaybackBackend();
        var persistCallCount = 0;
        var service = new ResumePlaybackService(
            initialEntries: [],
            persistEntries: _ => persistCallCount++);
        using var coordinator = new ResumeTrackingCoordinator(backend, service);

        await backend.LoadAsync("C:\\Media\\movie.mp4", CancellationToken.None);
        coordinator.ResetForMedia("C:\\Media\\movie.mp4");
        backend.ClockSnapshot = new ClockSnapshot(
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(90),
            1.0,
            false,
            true,
            DateTimeOffset.UtcNow);

        Assert.Equal(0, persistCallCount);
        Assert.Empty(service.Entries);
    }

    [Fact]
    public async Task ShellController_EnqueuesLoadsAndAdvancesPlaylistItems()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var firstPath = Path.Combine(directory.FullName, "first.mp4");
            var secondPath = Path.Combine(directory.FullName, "second.mp4");
            File.WriteAllText(firstPath, string.Empty);
            File.WriteAllText(secondPath, string.Empty);
            File.WriteAllText(Path.ChangeExtension(firstPath, ".srt"), "1\n00:00:00,000 --> 00:00:01,000\nHello\n");
            File.WriteAllText(Path.ChangeExtension(secondPath, ".srt"), "1\n00:00:00,000 --> 00:00:01,000\nWorld\n");

            var queue = new PlaybackQueueController();
            var backend = new FakeShellPlaybackBackend();
            using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
            using var shell = CreateShellController(
                queue,
                backend,
                workflow,
                new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

            var queueResult = shell.EnqueueFiles([firstPath, secondPath], autoplay: true);
            var loaded = await shell.LoadPlaybackItemAsync(
                queueResult.ItemToLoad,
                new ShellLoadMediaOptions
                {
                    ResumeEnabled = false,
                    PreviousPlaybackState = new PlaybackStateSnapshot()
                },
                CancellationToken.None);
            backend.ClockSnapshot = new ClockSnapshot(
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(10),
                1.0,
                false,
                true,
                DateTimeOffset.UtcNow);
            var ended = shell.HandleMediaEnded(resumeEnabled: true);

            Assert.True(loaded);
            Assert.Equal(firstPath, backend.LoadedPaths[0]);
            Assert.Equal(secondPath, queue.NowPlayingItem?.Path);
            Assert.Equal(secondPath, ended.NextItem?.Path);
            Assert.Equal(firstPath, queue.HistoryItems[0].Path);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ShellController_ExposesAndMutatesQueueStateWithoutWindowAccess()
    {
        var queue = new PlaybackQueueController();
        var backend = new FakeShellPlaybackBackend();
        using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
        using var shell = CreateShellController(
            queue,
            backend,
            workflow,
            new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

        shell.EnqueueFiles(["first.mp4", "second.mp4"], autoplay: false);
        shell.RemoveQueueItemAt(0);

        Assert.Single(shell.QueueItems);
        Assert.Equal("second.mp4", shell.QueueItems[0].Path);
        Assert.Null(shell.NowPlayingItem);

        shell.ClearQueue();

        Assert.Empty(shell.QueueItems);
        Assert.Null(shell.NowPlayingItem);
    }

    [Fact]
    public async Task ShellController_HandleMediaOpenedAppliesResumeThroughBackend()
    {
        var queue = new PlaybackQueueController();
        queue.PlayNow("C:\\Media\\movie.mp4");
        var backend = new FakeShellPlaybackBackend();
        using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
        var resumeEntry = new PlaybackResumeEntry
        {
            Path = "C:\\Media\\movie.mp4",
            PositionSeconds = 125,
            DurationSeconds = 3600,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        using var shell = CreateShellController(
            queue,
            backend,
            workflow,
            new ResumePlaybackService(initialEntries: [resumeEntry], persistEntries: _ => { }));

        var result = await shell.HandleMediaOpenedAsync(
            new PlaybackStateSnapshot
            {
                Path = "C:\\Media\\movie.mp4",
                Duration = TimeSpan.FromMinutes(60)
            },
            new ShellPreferencesSnapshot
            {
                ResumeEnabled = true
            });

        Assert.Equal(TimeSpan.FromSeconds(125), result.ResumePosition);
        Assert.Equal(TimeSpan.FromSeconds(125), backend.LastSeekPosition);
    }

    [Fact]
    public async Task ShellController_CaptionStartupGatePausesAndResumesThroughBackend()
    {
        var queue = new PlaybackQueueController();
        var backend = new FakeShellPlaybackBackend
        {
            ClockSnapshot = new ClockSnapshot(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), 1.0, false, true, DateTimeOffset.UtcNow)
        };
        using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
        using var shell = CreateShellController(
            queue,
            backend,
            workflow,
            new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

        var pauseResult = await shell.EvaluateCaptionStartupGateAsync(
            new SubtitleWorkflowSnapshot
            {
                CurrentVideoPath = "C:\\Media\\movie.mp4",
                SubtitleSource = SubtitlePipelineSource.Generated,
                IsCaptionGenerationInProgress = true,
                Cues = []
            },
            new PlaybackStateSnapshot
            {
                Path = "C:\\Media\\movie.mp4",
                Position = TimeSpan.FromSeconds(1)
            });

        var resumeResult = await shell.EvaluateCaptionStartupGateAsync(
            new SubtitleWorkflowSnapshot
            {
                CurrentVideoPath = "C:\\Media\\movie.mp4",
                SubtitleSource = SubtitlePipelineSource.Generated,
                IsCaptionGenerationInProgress = true,
                Cues =
                [
                    new ShellSubtitleCue
                    {
                        Start = TimeSpan.Zero,
                        End = TimeSpan.FromSeconds(2),
                        SourceText = "Hello"
                    }
                ]
            },
            new PlaybackStateSnapshot
            {
                Path = "C:\\Media\\movie.mp4",
                Position = TimeSpan.FromSeconds(1)
            });

        Assert.Equal("Generating initial captions before playback starts.", pauseResult.StatusMessage);
        Assert.Equal(1, backend.PauseCallCount);
        Assert.Equal("Captions ready. Playing with generated subtitles.", resumeResult.StatusMessage);
        Assert.Equal(1, backend.PlayCallCount);
        Assert.Equal(TimeSpan.Zero, backend.LastSeekPosition);
    }

    [Fact]
    public async Task ShellController_SelectEmbeddedSubtitleTrackAsync_UsesDirectPlaybackForImageTracks()
    {
        var queue = new PlaybackQueueController();
        var backend = new FakeShellPlaybackBackend();
        using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
        using var shell = CreateShellController(
            queue,
            backend,
            workflow,
            new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

        var result = await shell.SelectEmbeddedSubtitleTrackAsync(
            "C:\\Media\\movie.mkv",
            SubtitlePipelineSource.None,
            new ShellMediaTrack
            {
                Id = 7,
                Kind = ShellMediaTrackKind.Subtitle,
                IsTextBased = false
            });

        Assert.True(result.TrackSelectionChanged);
        Assert.Equal(7, result.SelectedSubtitleTrackId);
        Assert.False(result.IsError);
        Assert.Equal(7, backend.LastSubtitleTrackId);
        Assert.Equal("Selected image-based embedded subtitle track for direct playback.", result.StatusMessage);
    }

    [Fact]
    public async Task ShellController_SelectEmbeddedSubtitleTrackAsync_ReturnsErrorForTextTrackWithoutCurrentMedia()
    {
        var queue = new PlaybackQueueController();
        var backend = new FakeShellPlaybackBackend();
        using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
        using var shell = CreateShellController(
            queue,
            backend,
            workflow,
            new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

        var result = await shell.SelectEmbeddedSubtitleTrackAsync(
            null,
            SubtitlePipelineSource.None,
            new ShellMediaTrack
            {
                Id = 4,
                Kind = ShellMediaTrackKind.Subtitle,
                IsTextBased = true
            });

        Assert.False(result.TrackSelectionChanged);
        Assert.True(result.IsError);
        Assert.Equal("Open a video first.", result.StatusMessage);
        Assert.Equal(0, backend.SubtitleTrackSetCallCount);
    }

    [Fact]
    public async Task ShellController_SelectEmbeddedSubtitleTrackAsync_DisablingEmbeddedTrackReloadsMediaSubtitles()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var videoPath = Path.Combine(directory.FullName, "movie.mkv");
            var sidecarPath = Path.ChangeExtension(videoPath, ".srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, "1\n00:00:00,000 --> 00:00:01,000\nHello\n");

            var queue = new PlaybackQueueController();
            var backend = new FakeShellPlaybackBackend();
            var workflowStore = new InMemorySubtitleWorkflowStateStore();
            var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var credentialFacade = new CredentialFacade(new FakeCredentialStore());
            var subtitleSourceResolver = new FakeSubtitleSourceResolver();
            using var service = new SubtitleApplicationService(
                subtitleSourceResolver,
                new FakeCaptionGenerator(),
                new FakeSubtitleTranslator(),
                new FakeAiCredentialCoordinator(),
                new FakeRuntimeProvisioner(),
                credentialFacade,
                mediaSessionCoordinator,
                workflowStore,
                CreateProviderAvailabilityService(credentialFacade, _ => null));
            using var projectionAdapter = new SubtitleWorkflowProjectionAdapter(workflowStore, mediaSessionCoordinator.Store);
            using var workflow = new SubtitleWorkflowController(service, projectionAdapter, new SubtitlePresentationProjector());
            using var shell = CreateShellController(
                queue,
                backend,
                workflow,
                new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

            var result = await shell.SelectEmbeddedSubtitleTrackAsync(
                videoPath,
                SubtitlePipelineSource.EmbeddedTrack,
                track: null);

            Assert.True(result.TrackSelectionChanged);
            Assert.False(result.IsError);
            Assert.Equal("Embedded subtitle track disabled.", result.StatusMessage);
            Assert.Null(result.SelectedSubtitleTrackId);
            Assert.Single(backend.SubtitleTrackSelections);
            Assert.Null(backend.SubtitleTrackSelections[0]);
            Assert.Equal(1, subtitleSourceResolver.ExternalCalls);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void SubtitleWorkflowController_CanBeConstructedFromInjectedCollaboratorsOnly()
    {
        var workflowStore = new InMemorySubtitleWorkflowStateStore();
        var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var credentialFacade = new CredentialFacade(new FakeCredentialStore());
        using var service = new SubtitleApplicationService(
            new FakeSubtitleSourceResolver(),
            new FakeCaptionGenerator(),
            new FakeSubtitleTranslator(),
            new FakeAiCredentialCoordinator(),
            new FakeRuntimeProvisioner(),
            credentialFacade,
            mediaSessionCoordinator,
            workflowStore,
            CreateProviderAvailabilityService(credentialFacade, _ => null));
        using var projectionAdapter = new SubtitleWorkflowProjectionAdapter(workflowStore, mediaSessionCoordinator.Store);
        using var controller = new SubtitleWorkflowController(service, projectionAdapter, new SubtitlePresentationProjector());

        Assert.Same(service.MediaSessionStore, controller.MediaSessionStore);
        Assert.Equal(controller.Snapshot.CurrentVideoPath, projectionAdapter.Current.CurrentVideoPath);
    }

    [Fact]
    public void SubtitleWorkflowController_ImplementsShellBoundaryInterface()
    {
        using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);

        var shellService = Assert.IsAssignableFrom<ISubtitleWorkflowShellService>(workflow);

        Assert.Equal(workflow.Current.CurrentVideoPath, shellService.Current.CurrentVideoPath);
    }

    [Fact]
    public void SubtitleWorkflowController_TracksSourceOnlyOverrideAndClearsItWhenWorkflowChanges()
    {
        var workflowStore = new InMemorySubtitleWorkflowStateStore();
        var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        var credentialFacade = new CredentialFacade(new FakeCredentialStore());
        using var service = new SubtitleApplicationService(
            new FakeSubtitleSourceResolver(),
            new FakeCaptionGenerator(),
            new FakeSubtitleTranslator(),
            new FakeAiCredentialCoordinator(),
            new FakeRuntimeProvisioner(),
            credentialFacade,
            mediaSessionCoordinator,
            workflowStore,
            CreateProviderAvailabilityService(credentialFacade, _ => null));
        using var projectionAdapter = new SubtitleWorkflowProjectionAdapter(workflowStore, mediaSessionCoordinator.Store);
        using var controller = new SubtitleWorkflowController(service, projectionAdapter, new SubtitlePresentationProjector());

        workflowStore.Update(state => state with { CurrentVideoPath = "C:\\Media\\sample.mp4" });
        mediaSessionCoordinator.SetTranslationState(enabled: true, autoTranslateEnabled: false);

        var selected = controller.SelectRenderMode(SubtitleRenderMode.SourceOnly, SubtitleRenderMode.TranslationOnly);
        var hidden = controller.ToggleSubtitleVisibility(SubtitleRenderMode.TranslationOnly);
        var restored = controller.ToggleSubtitleVisibility(SubtitleRenderMode.Off);

        workflowStore.Update(state => state with { CurrentVideoPath = "C:\\Media\\other.mp4" });
        var effectiveAfterChange = controller.GetEffectiveRenderMode(SubtitleRenderMode.TranslationOnly);

        Assert.Equal(SubtitleRenderMode.TranslationOnly, selected.RequestedRenderMode);
        Assert.Equal(SubtitleRenderMode.SourceOnly, selected.EffectiveRenderMode);
        Assert.Equal(SubtitleRenderMode.Off, hidden.RequestedRenderMode);
        Assert.Equal(SubtitleRenderMode.TranslationOnly, restored.RequestedRenderMode);
        Assert.Equal(SubtitleRenderMode.SourceOnly, restored.EffectiveRenderMode);
        Assert.Equal(SubtitleRenderMode.TranslationOnly, effectiveAfterChange);
    }

    [Fact]
    public async Task ShellController_CurrentPlaybackSnapshotReflectsBackendState()
    {
        var queue = new PlaybackQueueController();
        var backend = new FakeShellPlaybackBackend();
        using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
        using var shell = CreateShellController(
            queue,
            backend,
            workflow,
            new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

        await backend.LoadAsync("C:\\Media\\movie.mp4", CancellationToken.None);
        backend.SetState(new PlaybackBackendState
        {
            Path = "C:\\Media\\movie.mp4",
            HasVideo = true,
            HasAudio = true,
            VideoWidth = 1920,
            VideoHeight = 1080,
            VideoDisplayWidth = 1920,
            VideoDisplayHeight = 1080,
            Volume = 0.5,
            ActiveHardwareDecoder = "d3d11va"
        });
        backend.ClockSnapshot = new ClockSnapshot(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(10),
            1.25,
            false,
            true,
            DateTimeOffset.UtcNow);

        var snapshot = shell.CurrentPlaybackSnapshot;

        Assert.Equal("C:\\Media\\movie.mp4", snapshot.Path);
        Assert.Equal(TimeSpan.FromSeconds(30), snapshot.Position);
        Assert.Equal(TimeSpan.FromMinutes(10), snapshot.Duration);
        Assert.Equal(0.5, snapshot.Volume);
        Assert.Equal("d3d11va", snapshot.ActiveHardwareDecoder);
        Assert.False(snapshot.IsPaused);
        Assert.True(snapshot.IsSeekable);
    }

    [Fact]
    public async Task ShellController_LoadPlaybackItemAsync_AppliesBothVolumeAndMute()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var filePath = Path.Combine(directory.FullName, "test.mp4");
            File.WriteAllText(filePath, string.Empty);

            var queue = new PlaybackQueueController();
            var backend = new FakeShellPlaybackBackend();
            using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
            using var shell = CreateShellController(
                queue,
                backend,
                workflow,
                new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

            var item = queue.PlayNow(filePath);
            var loaded = await shell.LoadPlaybackItemAsync(
                ToShellPlaylistItem(item),
                new ShellLoadMediaOptions
                {
                    Volume = 0.65,
                    IsMuted = true,
                    ResumeEnabled = false,
                    PreviousPlaybackState = new PlaybackStateSnapshot()
                },
                CancellationToken.None);

            Assert.True(loaded);
            Assert.Equal(0.65, backend.LastVolume);
            Assert.True(backend.LastMuted);
            Assert.Single(backend.VolumeHistory);
            Assert.Single(backend.MuteHistory);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ShellController_LoadPlaybackItemAsync_AppliesUnmutedWhenNotMuted()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var filePath = Path.Combine(directory.FullName, "test.mp4");
            File.WriteAllText(filePath, string.Empty);

            var queue = new PlaybackQueueController();
            var backend = new FakeShellPlaybackBackend();
            using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
            using var shell = CreateShellController(
                queue,
                backend,
                workflow,
                new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

            var item = queue.PlayNow(filePath);
            await shell.LoadPlaybackItemAsync(
                ToShellPlaylistItem(item),
                new ShellLoadMediaOptions
                {
                    Volume = 0.5,
                    IsMuted = false,
                    ResumeEnabled = false,
                    PreviousPlaybackState = new PlaybackStateSnapshot()
                },
                CancellationToken.None);

            Assert.Equal(0.5, backend.LastVolume);
            Assert.False(backend.LastMuted);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ShellController_ApplyAudioPreferencesAsync_SetsBothVolumeAndMuteAtomically()
    {
        var queue = new PlaybackQueueController();
        var backend = new FakeShellPlaybackBackend();
        using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
        using var shell = CreateShellController(
            queue,
            backend,
            workflow,
            new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

        await shell.ApplyAudioPreferencesAsync(0.3, true);

        Assert.Equal(0.3, backend.LastVolume);
        Assert.True(backend.LastMuted);
        Assert.Single(backend.VolumeHistory);
        Assert.Single(backend.MuteHistory);

        await shell.ApplyAudioPreferencesAsync(0.9, false);

        Assert.Equal(0.9, backend.LastVolume);
        Assert.False(backend.LastMuted);
        Assert.Equal(2, backend.VolumeHistory.Count);
        Assert.Equal(2, backend.MuteHistory.Count);
    }

    [Fact]
    public void ShellProjection_ReflectsBackendDrivenAudioState()
    {
        var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        using var projectionService = new ShellProjectionService(coordinator.Store);

        coordinator.ApplyPlaybackState(new PlaybackBackendState
        {
            Path = "C:\\Media\\sample.mp4",
            HasAudio = true,
            Volume = 0.42,
            IsMuted = true
        });

        var projection = projectionService.Current;

        Assert.Equal(0.42, projection.Transport.Volume);
        Assert.True(projection.Transport.IsMuted);

        coordinator.ApplyPlaybackState(new PlaybackBackendState
        {
            Path = "C:\\Media\\sample.mp4",
            HasAudio = true,
            Volume = 0.75,
            IsMuted = false
        });

        projection = projectionService.Current;

        Assert.Equal(0.75, projection.Transport.Volume);
        Assert.False(projection.Transport.IsMuted);
    }

    [Fact]
    public async Task ShellController_LoadPlaybackItemAsync_AppliesPlaybackRateAndDelays()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var filePath = Path.Combine(directory.FullName, "test.mp4");
            File.WriteAllText(filePath, string.Empty);

            var queue = new PlaybackQueueController();
            var backend = new FakeShellPlaybackBackend();
            using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
            using var shell = CreateShellController(
                queue,
                backend,
                workflow,
                new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

            var item = queue.PlayNow(filePath);
            var loaded = await shell.LoadPlaybackItemAsync(
                ToShellPlaylistItem(item),
                new ShellLoadMediaOptions
                {
                    Volume = 0.5,
                    IsMuted = false,
                    PlaybackRate = 1.5,
                    SubtitleDelaySeconds = 0.25,
                    AudioDelaySeconds = -0.10,
                    ResumeEnabled = false,
                    PreviousPlaybackState = new PlaybackStateSnapshot()
                },
                CancellationToken.None);

            Assert.True(loaded);
            Assert.Equal(1.5, backend.LastPlaybackRate);
            Assert.Equal(0.25, backend.LastSubtitleDelay);
            Assert.Equal(-0.10, backend.LastAudioDelay);
            Assert.Single(backend.PlaybackRateHistory);
            Assert.Single(backend.SubtitleDelayHistory);
            Assert.Single(backend.AudioDelayHistory);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ShellController_SetPlaybackRateAsync_DelegatesToBackend()
    {
        var queue = new PlaybackQueueController();
        var backend = new FakeShellPlaybackBackend();
        using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
        using var shell = CreateShellController(
            queue,
            backend,
            workflow,
            new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

        await shell.SetPlaybackRateAsync(0.75);
        Assert.Equal(0.75, backend.LastPlaybackRate);

        await shell.SetSubtitleDelayAsync(0.50);
        Assert.Equal(0.50, backend.LastSubtitleDelay);

        await shell.SetAudioDelayAsync(-0.15);
        Assert.Equal(-0.15, backend.LastAudioDelay);
    }

    [Fact]
    public void ShellPreferencesService_LoadsAndProjectsPersistedSettings()
    {
        var customSettings = new AppPlayerSettings
        {
            HardwareDecodingMode = BabelPlayer.Core.HardwareDecodingMode.D3D11,
            VolumeLevel = 0.65,
            IsMuted = true,
            DefaultPlaybackRate = 1.25,
            AudioDelaySeconds = 0.3,
            SubtitleDelaySeconds = -0.2,
            AspectRatioOverride = "16:9",
            SubtitleRenderMode = BabelPlayer.Core.SubtitleRenderMode.Off,
            SubtitleStyle = new BabelPlayer.Core.SubtitleStyleSettings
            {
                SourceFontSize = 28,
                TranslationFontSize = 30
            },
            ShortcutProfile = new BabelPlayer.Core.ShortcutProfile
            {
                Bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["play_pause"] = "Space"
                }
            },
            ResumeEnabled = true,
            PinnedRoots = ["C:\\FakeVideos"],
            ShowBrowserPanel = true,
            ShowPlaylistPanel = false,
            WindowMode = BabelPlayer.Core.PlaybackWindowMode.PictureInPicture
        };
        var settingsFacade = new TestSettingsFacade(customSettings);
        var service = new ShellPreferencesService(settingsFacade);

        var snapshot = service.Current;

        Assert.Equal(0.65, snapshot.VolumeLevel);
        Assert.True(snapshot.IsMuted);
        Assert.Equal(1.25, snapshot.PlaybackRate);
        Assert.Equal(0.3, snapshot.AudioDelaySeconds);
        Assert.Equal(-0.2, snapshot.SubtitleDelaySeconds);
        Assert.Equal("16:9", snapshot.AspectRatio);
        Assert.Equal(HardwareDecodingMode.D3D11, snapshot.HardwareDecodingMode);
        Assert.Equal(SubtitleRenderMode.Off, snapshot.SubtitleRenderMode);
        Assert.Equal(SubtitleRenderMode.TranslationOnly, snapshot.LastNonOffSubtitleRenderMode);
        Assert.False(snapshot.ShowSubtitleSource);
        Assert.True(snapshot.ResumeEnabled);
        Assert.Equal(["C:\\FakeVideos"], snapshot.PinnedRoots);
        Assert.True(snapshot.ShowBrowserPanel);
        Assert.False(snapshot.ShowPlaylistPanel);
        Assert.Equal(PlaybackWindowMode.PictureInPicture, snapshot.WindowMode);
        Assert.Equal("Space", snapshot.ShortcutProfile.Bindings["play_pause"]);
    }

    [Fact]
    public void ShellPreferencesService_ApplyChange_PersistsOnlyRequestedFields()
    {
        var initialSettings = new AppPlayerSettings
        {
            HardwareDecodingMode = BabelPlayer.Core.HardwareDecodingMode.AutoSafe,
            SubtitleRenderMode = BabelPlayer.Core.SubtitleRenderMode.Dual,
            SubtitleStyle = new BabelPlayer.Core.SubtitleStyleSettings
            {
                SourceFontSize = 31,
                TranslationFontSize = 33
            },
            PinnedRoots = ["C:\\Media"],
            VolumeLevel = 0.65,
            IsMuted = true,
            DefaultPlaybackRate = 1.0,
            AudioDelaySeconds = 0.1,
            SubtitleDelaySeconds = -0.2,
            AspectRatioOverride = "4:3",
            ShowBrowserPanel = true,
            ShowPlaylistPanel = false,
            ResumeEnabled = true,
            WindowMode = BabelPlayer.Core.PlaybackWindowMode.Standard
        };
        var settingsFacade = new TestSettingsFacade(initialSettings);
        var service = new ShellPreferencesService(settingsFacade);

        var snapshot = service.ApplyPlaybackDefaultsChange(new ShellPlaybackDefaultsChange(
            HardwareDecodingMode.D3D11,
            1.5,
            0.25,
            -0.35,
            ""));

        Assert.Equal(HardwareDecodingMode.D3D11, snapshot.HardwareDecodingMode);
        Assert.Equal(1.5, snapshot.PlaybackRate);
        Assert.Equal(0.25, snapshot.AudioDelaySeconds);
        Assert.Equal(-0.35, snapshot.SubtitleDelaySeconds);
        Assert.Equal("auto", snapshot.AspectRatio);
        Assert.Equal(SubtitleRenderMode.Dual, snapshot.SubtitleRenderMode);
        Assert.Equal(31, snapshot.SubtitleStyle.SourceFontSize);
        Assert.Equal(["C:\\Media"], snapshot.PinnedRoots);
        Assert.Equal(0.65, snapshot.VolumeLevel);
        Assert.True(snapshot.IsMuted);
        Assert.True(snapshot.ShowBrowserPanel);
        Assert.False(snapshot.ShowPlaylistPanel);
        Assert.Equal(PlaybackWindowMode.Standard, snapshot.WindowMode);

        Assert.NotNull(settingsFacade.SavedSettings);
        Assert.Equal(BabelPlayer.Core.HardwareDecodingMode.D3D11, settingsFacade.SavedSettings!.HardwareDecodingMode);
        Assert.Equal(1.5, settingsFacade.SavedSettings.DefaultPlaybackRate);
        Assert.Equal(0.25, settingsFacade.SavedSettings.AudioDelaySeconds);
        Assert.Equal(-0.35, settingsFacade.SavedSettings.SubtitleDelaySeconds);
        Assert.Equal("auto", settingsFacade.SavedSettings.AspectRatioOverride);
        Assert.Equal(BabelPlayer.Core.SubtitleRenderMode.Dual, settingsFacade.SavedSettings.SubtitleRenderMode);
        Assert.Equal(31, settingsFacade.SavedSettings.SubtitleStyle.SourceFontSize);
    }

    [Fact]
    public void ShellPreferencesService_ApplyShortcutProfile_ReplacesProfileWithoutMutatingSnapshot()
    {
        var initialProfile = new ShortcutProfile
        {
            Bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["play_pause"] = "Space"
            }
        };
        var settingsFacade = new TestSettingsFacade(new AppPlayerSettings
        {
            ShortcutProfile = new BabelPlayer.Core.ShortcutProfile
            {
                Bindings = new Dictionary<string, string>(initialProfile.Bindings, StringComparer.OrdinalIgnoreCase)
            },
            PinnedRoots = ["C:\\Media"]
        });
        var service = new ShellPreferencesService(settingsFacade);
        var before = service.Current;
        var updatedProfile = new ShortcutProfile
        {
            Bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["play_pause"] = "Ctrl+P"
            }
        };

        var after = service.ApplyShortcutProfileChange(new ShellShortcutProfileChange(updatedProfile));

        Assert.Equal("Space", before.ShortcutProfile.Bindings["play_pause"]);
        Assert.Equal("Ctrl+P", after.ShortcutProfile.Bindings["play_pause"]);
        Assert.NotSame(before, after);
        Assert.Equal(updatedProfile.Bindings, after.ShortcutProfile.Bindings);
    }

    [Fact]
    public void ShellPreferencesService_PublishesSnapshotChangedAfterMutation()
    {
        var service = new ShellPreferencesService(new TestSettingsFacade(new AppPlayerSettings
        {
            PinnedRoots = ["C:\\Media"]
        }));
        ShellPreferencesSnapshot? published = null;
        service.SnapshotChanged += snapshot => published = snapshot;

        var updated = service.ApplyAudioStateChange(new ShellAudioStateChange(0.42, true));

        Assert.Same(updated, published);
        Assert.NotNull(published);
        Assert.Equal(0.42, published!.VolumeLevel);
        Assert.True(published.IsMuted);
    }

    [Fact]
    public void ShellLibraryService_InitializesFromPinnedRoots()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "movie.mp4"), string.Empty);
            var childFolderPath = Directory.CreateDirectory(Path.Combine(root.FullName, "Season 1")).FullName;

            var preferences = new ShellPreferencesService(new TestSettingsFacade(new AppPlayerSettings
            {
                PinnedRoots = [root.FullName]
            }));
            var service = new ShellLibraryService(new LibraryBrowserService(), preferences);

            var snapshot = service.Current;
            var rootEntry = Assert.Single(snapshot.Roots);

            Assert.Equal(root.FullName, rootEntry.Path);
            Assert.True(rootEntry.IsFolder);
            Assert.Contains(rootEntry.Children, child => string.Equals(child.Path, childFolderPath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(rootEntry.Children, child => string.Equals(child.Path, Path.Combine(root.FullName, "movie.mp4"), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ShellLibraryService_PinAndUnpinRoot_UpdateSnapshotAndPersistPinnedRoots()
    {
        var rootA = Directory.CreateTempSubdirectory();
        var rootB = Directory.CreateTempSubdirectory();
        try
        {
            var settingsFacade = new TestSettingsFacade(new AppPlayerSettings
            {
                PinnedRoots = [rootA.FullName]
            });
            var preferences = new ShellPreferencesService(settingsFacade);
            var service = new ShellLibraryService(new LibraryBrowserService(), preferences);

            var pinResult = service.PinRoot(rootB.FullName);
            Assert.False(pinResult.IsError);
            Assert.Equal(2, pinResult.Snapshot.Roots.Count);
            Assert.Contains(preferences.Current.PinnedRoots, path => string.Equals(path, rootB.FullName, StringComparison.OrdinalIgnoreCase));

            var unpinResult = service.UnpinRoot(rootA.FullName);
            Assert.False(unpinResult.IsError);
            Assert.Single(unpinResult.Snapshot.Roots);
            Assert.DoesNotContain(preferences.Current.PinnedRoots, path => string.Equals(path, rootA.FullName, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(settingsFacade.SavedSettings);
            Assert.Equal([rootB.FullName], settingsFacade.SavedSettings!.PinnedRoots);
        }
        finally
        {
            rootA.Delete(recursive: true);
            rootB.Delete(recursive: true);
        }
    }

    [Fact]
    public void ShellLibraryService_SetExpanded_RealizesChildrenAndPreservesExpansionState()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var seasonPath = Directory.CreateDirectory(Path.Combine(root.FullName, "Season 1")).FullName;
            File.WriteAllText(Path.Combine(seasonPath, "episode-01.mp4"), string.Empty);
            var preferences = new ShellPreferencesService(new TestSettingsFacade(new AppPlayerSettings
            {
                PinnedRoots = [root.FullName]
            }));
            var service = new ShellLibraryService(new LibraryBrowserService(), preferences);

            var beforeExpand = Assert.Single(service.Current.Roots)
                .Children
                .Single(child => string.Equals(child.Path, seasonPath, StringComparison.OrdinalIgnoreCase));
            Assert.False(beforeExpand.IsExpanded);
            Assert.Empty(beforeExpand.Children);
            Assert.True(beforeExpand.HasUnrealizedChildren);

            var result = service.SetExpanded(seasonPath, true);
            var expanded = Assert.Single(result.Snapshot.Roots)
                .Children
                .Single(child => string.Equals(child.Path, seasonPath, StringComparison.OrdinalIgnoreCase));

            Assert.True(expanded.IsExpanded);
            Assert.False(expanded.HasUnrealizedChildren);
            Assert.Single(expanded.Children);
            Assert.Equal(Path.Combine(seasonPath, "episode-01.mp4"), expanded.Children[0].Path);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void SubtitleWorkflowProjectionAdapter_ProjectsAvailableModelOptions()
    {
        var workflowStore = new InMemorySubtitleWorkflowStateStore();
        var mediaCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        using var adapter = new SubtitleWorkflowProjectionAdapter(workflowStore, mediaCoordinator.Store);

        var snapshot = adapter.Current;

        Assert.Equal(
            SubtitleWorkflowCatalog.AvailableTranscriptionModels.Select(model => model.Key),
            snapshot.AvailableTranscriptionModels.Select(model => model.Key));
        Assert.Equal(
            SubtitleWorkflowCatalog.AvailableTranslationModels.Select(model => model.Key),
            snapshot.AvailableTranslationModels.Select(model => model.Key));
    }

    [Fact]
    public void SubtitleWorkflowProjectionAdapter_CanonicalizesLegacyTranscriptionKeys()
    {
        var workflowStore = new InMemorySubtitleWorkflowStateStore();
        var mediaCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
        using var adapter = new SubtitleWorkflowProjectionAdapter(workflowStore, mediaCoordinator.Store);

        workflowStore.Update(state => state with
        {
            SelectedTranscriptionModelKey = "local:small"
        });

        Assert.Equal("local:small-multilingual", adapter.Current.SelectedTranscriptionModelKey);
        Assert.Equal("Local Small (multilingual)", adapter.Current.SelectedTranscriptionLabel);
    }

    [Fact]
    public async Task ShortcutCommandExecutor_RoutesQueuePlaybackAndSubtitleCommands()
    {
        var preferences = new ShellPreferencesService(new TestSettingsFacade(new AppPlayerSettings
        {
            DefaultPlaybackRate = 1.0,
            VolumeLevel = 0.8,
            PinnedRoots = ["C:\\Media"]
        }));
        var queueCommands = new FakeQueueCommands();
        var playbackCommands = new FakeShortcutPlaybackCommands
        {
            CurrentPlaybackSnapshot = new PlaybackStateSnapshot { IsPaused = true }
        };
        using var workflow = TestWorkflowControllerFactory.Create(
            credentialFacade: new CredentialFacade(new FakeCredentialStore()),
            providerAvailabilityService: new FakeProviderAvailabilityService
            {
                ConfiguredProviders = new Dictionary<TranslationProvider, bool>
                {
                    [TranslationProvider.DeepL] = true
                }
            },
            aiCredentialCoordinator: new FakeAiCredentialCoordinator());
        await workflow.SelectTranslationModelAsync("cloud:deepl");
        var executor = new ShortcutCommandExecutor(
            queueCommands,
            playbackCommands,
            preferences,
            CreateShellPreferenceCommands(preferences, playbackCommands),
            workflow);

        var playPause = await executor.ExecuteAsync("play_pause");
        var speedUp = await executor.ExecuteAsync("speed_up");
        var nextItem = await executor.ExecuteAsync("next_item");
        var subtitleToggle = await executor.ExecuteAsync("subtitle_toggle");
        var translationToggle = await executor.ExecuteAsync("translation_toggle");

        Assert.Equal(1, playbackCommands.PlayCalls);
        Assert.Equal("Playback resumed.", playPause.StatusMessage);
        Assert.Equal(1.25, playbackCommands.LastPlaybackRate);
        Assert.Equal(1.25, preferences.Current.PlaybackRate);
        Assert.Equal("next.mp4", nextItem.ItemToLoad?.Path);
        Assert.Equal(1, queueCommands.MoveNextCalls);
        Assert.Equal(ShortcutShellAction.ToggleSubtitleVisibility, subtitleToggle.ShellAction);
        Assert.True(workflow.Snapshot.IsTranslationEnabled);
        Assert.Equal("Translation enabled.", translationToggle.StatusMessage);
    }

    [Fact]
    public void ShortcutProfileService_NormalizesBindingsAndFlagsConflicts()
    {
        var preferences = new ShellPreferencesService(new TestSettingsFacade(new AppPlayerSettings
        {
            ShortcutProfile = new BabelPlayer.Core.ShortcutProfile
            {
                Bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["play_pause"] = "Shift + Space",
                    ["mute"] = "Ctrl+",
                    ["custom"] = "Ctrl+K"
                }
            },
            PinnedRoots = ["C:\\Media"]
        }));
        using var service = new ShortcutProfileService(preferences);
        var conflictingProfile = new ShortcutProfile
        {
            Bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["play_pause"] = "Space",
                ["mute"] = "Space"
            }
        };

        var normalization = service.NormalizeProfile(preferences.Current.ShortcutProfile);
        var validation = service.ValidateProfile(conflictingProfile);

        Assert.Contains(normalization.NormalizedBindings, binding => binding.CommandId == "play_pause" && binding.NormalizedGesture == "Shift+Space");
        Assert.Contains("custom", normalization.UnsupportedCommandIds);
        Assert.Contains("mute", normalization.InvalidCommandIds);
        Assert.False(validation.IsValid);
        Assert.Single(validation.Conflicts);
    }

    [Fact]
    public async Task CredentialSetupService_ReportsReadinessAndDelegatesSetup()
    {
        var credentialFacade = new CredentialFacade(new ConfigurableCredentialStore
        {
            OpenAiApiKey = "openai-key",
            TranslationModelKey = "cloud:deepl",
            SubtitleModelKey = "cloud:gpt-4o-transcribe"
        });
        var providerAvailability = new FakeProviderAvailabilityService
        {
            ConfiguredProviders = new Dictionary<TranslationProvider, bool>
            {
                [TranslationProvider.DeepL] = true
            }
        };
        var credentialCoordinator = new FakeAiCredentialCoordinator();
        var runtimeProvisioner = new FakeRuntimeProvisioner();
        var service = new CredentialSetupService(
            credentialFacade,
            providerAvailability,
            credentialCoordinator,
            runtimeProvisioner,
            _ => null);
        CredentialSetupSnapshot? published = null;
        service.SnapshotChanged += snapshot => published = snapshot;

        var transcriptionAvailability = service.GetTranscriptionAvailability(SubtitleWorkflowCatalog.GetTranscriptionModel("cloud:gpt-4o-transcribe"));
        var translationAvailability = service.GetTranslationAvailability(SubtitleWorkflowCatalog.GetTranslationModel("local:hymt-1.8b"));
        var ensured = await service.EnsureTranslationProviderCredentialsAsync(TranslationProvider.DeepL);

        Assert.True(service.Current.HasOpenAiCredentials);
        Assert.True(service.Current.TranslationProviderConfigured[TranslationProvider.DeepL]);
        Assert.True(transcriptionAvailability.IsAvailable);
        Assert.False(translationAvailability.IsAvailable);
        Assert.True(translationAvailability.RequiresRuntimeBootstrap);
        Assert.True(ensured);
        Assert.Equal(1, credentialCoordinator.TranslationCalls);
        Assert.NotNull(published);
    }

    [Fact]
    public async Task ShellController_ImplementsQueueAndPlaybackBoundaryInterfaces()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var firstPath = Path.Combine(directory.FullName, "first.mp4");
            File.WriteAllText(firstPath, string.Empty);
            var queue = new PlaybackQueueController();
            var backend = new FakeShellPlaybackBackend();
            using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
            using var shell = CreateShellController(
                queue,
                backend,
                workflow,
                new ResumePlaybackService());

            var queueReader = Assert.IsAssignableFrom<IQueueProjectionReader>(shell);
            var queueCommands = Assert.IsAssignableFrom<IQueueCommands>(shell);
            var playbackCommands = Assert.IsAssignableFrom<IShellPlaybackCommands>(shell);

            var result = queueCommands.EnqueueFiles([firstPath], autoplay: true);
            var loaded = await playbackCommands.LoadPlaybackItemAsync(
                result.ItemToLoad,
                new ShellLoadMediaOptions(),
                CancellationToken.None);

            Assert.Equal(firstPath, result.ItemToLoad?.Path);
            Assert.Equal(firstPath, queueReader.QueueSnapshot.NowPlayingItem?.Path);
            Assert.True(loaded);
            Assert.Equal(firstPath, Assert.Single(backend.LoadedPaths));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AppBoundaryContracts_DoNotExposeWinUiTypes()
    {
        var appFiles = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BabelPlayer.App", "ShellCommandInterfaces.cs")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BabelPlayer.App", "CredentialSetupService.cs")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BabelPlayer.App", "ShortcutProfileService.cs")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BabelPlayer.App", "ShortcutCommandExecutor.cs"))
        };

        foreach (var file in appFiles)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("VirtualKey", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TreeViewNode", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AppWindow", source, StringComparison.Ordinal);
            Assert.DoesNotContain("HWND", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DirectX", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MainWindow_Source_NoLongerDirectlyReferencesSettingsLibraryCredentialsShortcutServicesConcreteSubtitleControllerOrStaticSubtitleCatalogLookups()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "BabelPlayer.WinUI",
            "MainWindow.xaml.cs"));
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("SettingsFacade", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryBrowserService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CredentialFacade", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShortcutService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SubtitleWorkflowController", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_subtitleSourceOnlyOverrideVideoPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_lastNonOffSubtitleRenderMode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectedAspectRatio", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_audioDelaySeconds", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_subtitleDelaySeconds", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SubtitleWorkflowCatalog.GetTranscriptionModel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SubtitleWorkflowCatalog.GetTranslationModel", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowPartials_DoNotDirectlyMutatePreferencesOrReferenceForbiddenConcreteWorkflowTypes()
    {
        var winUiDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "BabelPlayer.WinUI"));
        var mainWindowFiles = Directory.GetFiles(winUiDirectory, "MainWindow*.cs", SearchOption.TopDirectoryOnly);

        foreach (var file in mainWindowFiles)
        {
            var source = File.ReadAllText(file);

            Assert.DoesNotContain("_shellPreferencesService.Apply", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SettingsFacade", source, StringComparison.Ordinal);
            Assert.DoesNotContain("LibraryBrowserService", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CredentialFacade", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ShortcutService", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SubtitleWorkflowController", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RepoAndAgentInstructions_ReferenceShellBoundaryGuardrailsDocument()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var requiredReference = "docs/SHELL_BOUNDARY_GUARDRAILS.md";
        var instructionFiles = new[]
        {
            Path.Combine(repoRoot, "AGENTS.md"),
            Path.Combine(repoRoot, ".github", "copilot-instructions.md"),
            Path.Combine(repoRoot, ".github", "agents", "refactor-shell.md"),
            Path.Combine(repoRoot, ".github", "agents", "seam-test.md"),
            Path.Combine(repoRoot, ".github", "agents", "new-provider.md")
        };

        foreach (var file in instructionFiles)
        {
            var source = File.ReadAllText(file);
            Assert.Contains(requiredReference, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ShortcutEditorDialog_Source_NoLongerDirectlyReferencesShortcutService()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "BabelPlayer.WinUI",
            "ShortcutEditorDialog.cs"));
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("ShortcutService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WinUiProject_DoesNotReferenceCoreProject()
    {
        var projectPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "BabelPlayer.WinUI",
            "BabelPlayer.WinUI.csproj"));
        var source = File.ReadAllText(projectPath);

        Assert.DoesNotContain("BabelPlayer.Core.csproj", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WinUiProject_ReferencesInfrastructureProject()
    {
        var projectPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "BabelPlayer.WinUI",
            "BabelPlayer.WinUI.csproj"));
        var source = File.ReadAllText(projectPath);

        Assert.Contains("BabelPlayer.Infrastructure.csproj", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppProject_DoesNotReferenceInfrastructureProject()
    {
        var projectPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "BabelPlayer.App",
            "BabelPlayer.App.csproj"));
        var source = File.ReadAllText(projectPath);

        Assert.DoesNotContain("BabelPlayer.Infrastructure.csproj", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WinUiSources_DoNotImportCoreNamespace()
    {
        var winUiDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "BabelPlayer.WinUI"));
        var sourceFiles = Directory.GetFiles(winUiDirectory, "*.cs", SearchOption.AllDirectories);

        foreach (var file in sourceFiles)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("using BabelPlayer.Core;", source, StringComparison.Ordinal);
            Assert.DoesNotContain("BabelPlayer.Core.", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AppSources_DoNotConstructProviderOrRuntimeInfrastructureDirectly()
    {
        var appDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "BabelPlayer.App"));
        var sourceFiles = Directory.GetFiles(appDirectory, "*.cs", SearchOption.AllDirectories);
        var forbiddenPatterns = new[]
        {
            "new AsrService(",
            "new MtService(",
            "MtService.",
            "MpvRuntimeInstaller.InstallAsync",
            "FfmpegRuntimeInstaller.InstallAsync",
            "LlamaCppRuntimeInstaller.InstallAsync",
            "ProviderAvailabilityCompositionFactory.Create"
        };

        foreach (var file in sourceFiles)
        {
            var source = File.ReadAllText(file);
            foreach (var forbiddenPattern in forbiddenPatterns)
            {
                Assert.DoesNotContain(forbiddenPattern, source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AppXaml_Source_UsesTelemetrySeamInsteadOfCoreLoggingImplementations()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "BabelPlayer.WinUI",
            "App.xaml.cs"));
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("IAppTelemetryBootstrap", source, StringComparison.Ordinal);
        Assert.DoesNotContain("using BabelPlayer.Core;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BabelLogManager", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppDiagnosticsContext", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellCompositionRoot_OnlyReturnsInterfacesOrApprovedWinUiLocalServices()
    {
        var allowedConcreteTypes = new HashSet<Type>
        {
            typeof(BabelPlayer.WinUI.WinUIWindowModeService),
            typeof(BabelPlayer.WinUI.StageCoordinator)
        };
        var properties = typeof(BabelPlayer.WinUI.ShellDependencies).GetProperties();

        foreach (var property in properties)
        {
            var propertyType = property.PropertyType;
            var isAllowed = propertyType.IsInterface
                || propertyType == typeof(IDisposable)
                || allowedConcreteTypes.Contains(propertyType);

            Assert.True(isAllowed, $"ShellDependencies.{property.Name} exposes disallowed concrete type {propertyType.FullName}.");

            if (string.Equals(propertyType.Namespace, "BabelPlayer.App", StringComparison.Ordinal))
            {
                Assert.True(propertyType.IsInterface, $"ShellDependencies.{property.Name} must not return concrete App implementation {propertyType.FullName} outside the composition root.");
            }
        }
    }

    [Fact]
    public void ShellCompositionRoot_DelegatesProviderAndRuntimeAssemblyToInfrastructureFactory()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "BabelPlayer.WinUI",
            "ShellCompositionRoot.cs"));
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("ISubtitleWorkflowInfrastructureFactory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new RuntimeBootstrapService(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProviderCompositionFactory(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new AsrTranscriptionEngineFactory(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new MtTranslationEngineFactory(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new AiCredentialCoordinatorFactory(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new DefaultRuntimeProvisioner(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new DefaultSubtitleSourceResolver(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new DefaultCaptionGenerator(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProviderBackedSubtitleTranslator(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppViewModelDirectory_IsEmptyAfterMoveToWinUi()
    {
        var viewModelDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "BabelPlayer.App",
            "ViewModels"));

        if (!Directory.Exists(viewModelDirectory))
        {
            return;
        }

        Assert.Empty(Directory.GetFiles(viewModelDirectory, "*.cs", SearchOption.AllDirectories));
    }

    [Fact]
    public void AppPublicShellContractsAndDtos_DoNotExposeForbiddenTypes()
    {
        var rootTypes = new[]
        {
            typeof(IShellProjectionReader),
            typeof(ShellProjectionSnapshot),
            typeof(ShellTransportProjection),
            typeof(ShellSelectedTracksProjection),
            typeof(ShellSubtitleProjection),
            typeof(IPlaybackHostRuntime),
            typeof(IAppTelemetryBootstrap),
            typeof(IAppLogFactory),
            typeof(IAppLogger),
            typeof(IAppDiagnosticsState),
            typeof(AppDiagnosticsSnapshot),
            typeof(PlaybackDiagnosticsSummary),
            typeof(QueueDiagnosticsSummary),
            typeof(SubtitleWorkflowDiagnosticsSummary),
            typeof(IShellPreferencesService),
            typeof(ShellPreferencesSnapshot),
            typeof(ShellLayoutPreferencesChange),
            typeof(ShellPlaybackDefaultsChange),
            typeof(ShellSubtitlePresentationChange),
            typeof(ShellAudioStateChange),
            typeof(ShellShortcutProfileChange),
            typeof(ShellResumeEnabledChange),
            typeof(ShellPinnedRootsChange),
            typeof(IQueueProjectionReader),
            typeof(IQueueCommands),
            typeof(IShellPlaybackCommands),
            typeof(ISubtitleWorkflowShellService),
            typeof(SubtitleWorkflowSnapshot),
            typeof(SubtitleOverlayPresentation),
            typeof(TranscriptionModelSelection),
            typeof(TranslationModelSelection),
            typeof(IShellLibraryService),
            typeof(ShellLibrarySnapshot),
            typeof(LibraryEntrySnapshot),
            typeof(ShellLibraryMutationResult),
            typeof(ICredentialSetupService),
            typeof(CredentialSetupSnapshot),
            typeof(IShortcutProfileService),
            typeof(ShortcutProfileSnapshot),
            typeof(ShortcutActionDefinition),
            typeof(ShortcutProfileValidationResult),
            typeof(ShortcutProfileNormalizationResult),
            typeof(ShortcutConflict),
            typeof(ShortcutBindingSnapshot),
            typeof(IShortcutCommandExecutor),
            typeof(ShortcutCommandExecutionResult),
            typeof(ShellMediaTrackKind),
            typeof(ShellSubtitleRenderMode),
            typeof(ShellHardwareDecodingMode),
            typeof(ShellPlaybackWindowMode),
            typeof(ShellMediaTrack),
            typeof(ShellSubtitleStyle),
            typeof(ShellPlaybackStateSnapshot),
            typeof(ShellShortcutProfile),
            typeof(ShellPlaylistItem),
            typeof(ShellSubtitleCue),
            typeof(ShellRuntimeInstallProgress),
            typeof(ShellQueueSnapshot),
            typeof(ShellQueueMediaResult),
            typeof(ShellLoadMediaOptions),
            typeof(ShellWorkflowTransitionResult),
            typeof(ShellPlaybackOpenResult),
            typeof(ShellMediaEndedResult),
            typeof(ShellSubtitleTrackSelectionResult)
        };
        var visited = new HashSet<Type>();

        foreach (var rootType in rootTypes)
        {
            AssertTypeGraphIsShellSafe(rootType, visited);
        }
    }

    private static void AssertTypeGraphIsShellSafe(Type type, ISet<Type> visited)
    {
        foreach (var candidate in ExpandInspectedTypes(type))
        {
            if (!visited.Add(candidate))
            {
                continue;
            }

            Assert.False(IsForbiddenContractType(candidate), $"Forbidden type leaked through shell contract graph: {candidate.FullName}.");

            foreach (var nestedType in candidate.GetNestedTypes(System.Reflection.BindingFlags.Public))
            {
                AssertTypeGraphIsShellSafe(nestedType, visited);
            }

            foreach (var property in candidate.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
            {
                AssertTypeGraphIsShellSafe(property.PropertyType, visited);
            }

            foreach (var field in candidate.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
            {
                AssertTypeGraphIsShellSafe(field.FieldType, visited);
            }

            foreach (var @event in candidate.GetEvents(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
            {
                if (@event.EventHandlerType is not null)
                {
                    AssertTypeGraphIsShellSafe(@event.EventHandlerType, visited);
                }
            }

            foreach (var method in candidate.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                {
                    continue;
                }

                AssertTypeGraphIsShellSafe(method.ReturnType, visited);
                foreach (var parameter in method.GetParameters())
                {
                    AssertTypeGraphIsShellSafe(parameter.ParameterType, visited);
                }
            }
        }
    }

    private static IEnumerable<Type> ExpandInspectedTypes(Type type)
    {
        if (type == typeof(void))
        {
            yield break;
        }

        if (type.IsByRef || type.IsPointer || type.HasElementType)
        {
            var elementType = type.GetElementType();
            if (elementType is not null)
            {
                foreach (var element in ExpandInspectedTypes(elementType))
                {
                    yield return element;
                }
            }

            yield break;
        }

        if (type.IsGenericParameter)
        {
            yield break;
        }

        yield return type;

        foreach (var genericArgument in type.GetGenericArguments())
        {
            foreach (var expandedGenericArgument in ExpandInspectedTypes(genericArgument))
            {
                yield return expandedGenericArgument;
            }
        }
    }

    private static bool IsForbiddenContractType(Type type)
    {
        if (type.Assembly == typeof(string).Assembly)
        {
            return false;
        }

        var namespaceName = type.Namespace ?? string.Empty;
        var fullName = type.FullName ?? type.Name;
        return namespaceName.StartsWith("BabelPlayer.Core", StringComparison.Ordinal)
            || namespaceName.StartsWith("Whisper.net.Ggml", StringComparison.Ordinal)
            || namespaceName.StartsWith("Microsoft.UI", StringComparison.Ordinal)
            || namespaceName.StartsWith("Windows.", StringComparison.Ordinal)
            || namespaceName.StartsWith("WinRT", StringComparison.Ordinal)
            || fullName.Contains("HWND", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("DirectX", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("Mpv", StringComparison.OrdinalIgnoreCase);
    }

    private static ShellController CreateShellController(
        PlaybackQueueController queue,
        FakeShellPlaybackBackend backend,
        SubtitleWorkflowController workflow,
        ResumePlaybackService? resumePlaybackService = null)
    {
        return new ShellController(
            queue,
            backend,
            workflow,
            new LibraryBrowserService(),
            resumePlaybackService ?? new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }),
            CreateShellPreferencesService());
    }

    private static ShellPreferencesService CreateShellPreferencesService()
        => new(new TestSettingsFacade(new AppPlayerSettings()));

    private static ProviderAvailabilityService CreateProviderAvailabilityService(
        CredentialFacade credentialFacade,
        Func<string, string?> environmentVariableReader)
        => new(new ProviderCompositionFactory(), credentialFacade, environmentVariableReader);

    private static ShellPlaylistItem? ToShellPlaylistItem(PlaylistItem? item)
        => item is null
            ? null
            : new ShellPlaylistItem
            {
                Path = item.Path,
                DisplayName = item.DisplayName,
                IsDirectorySeed = item.IsDirectorySeed
            };

    private static IShellPreferenceCommands CreateShellPreferenceCommands(
        IShellPreferencesService preferences,
        IShellPlaybackCommands playbackCommands)
        => new ShellPreferenceCommands(preferences, playbackCommands, new FakeShortcutProfileService());

    private sealed class TestSettingsFacade : SettingsFacade
    {
        private AppPlayerSettings _settings;

        public TestSettingsFacade(AppPlayerSettings settings) => _settings = settings;

        public AppPlayerSettings? SavedSettings { get; private set; }

        public override AppPlayerSettings Load() => _settings;

        public override void Save(AppPlayerSettings settings)
        {
            _settings = settings;
            SavedSettings = settings;
        }
    }

    private sealed class ConfigurableCredentialStore : ICredentialStore
    {
        public string? OpenAiApiKey { get; set; }
        public string? GoogleTranslateApiKey { get; set; }
        public string? DeepLApiKey { get; set; }
        public string? MicrosoftTranslatorApiKey { get; set; }
        public string? MicrosoftTranslatorRegion { get; set; }
        public string? SubtitleModelKey { get; set; }
        public string? TranslationModelKey { get; set; }
        public bool AutoTranslateEnabled { get; set; }
        public string? LlamaCppServerPath { get; set; }
        public string? LlamaCppRuntimeVersion { get; set; }
        public string? LlamaCppRuntimeSource { get; set; }

        public string? GetOpenAiApiKey() => OpenAiApiKey;
        public void SaveOpenAiApiKey(string apiKey) => OpenAiApiKey = apiKey;
        public string? GetGoogleTranslateApiKey() => GoogleTranslateApiKey;
        public void SaveGoogleTranslateApiKey(string apiKey) => GoogleTranslateApiKey = apiKey;
        public string? GetDeepLApiKey() => DeepLApiKey;
        public void SaveDeepLApiKey(string apiKey) => DeepLApiKey = apiKey;
        public string? GetMicrosoftTranslatorApiKey() => MicrosoftTranslatorApiKey;
        public void SaveMicrosoftTranslatorApiKey(string apiKey) => MicrosoftTranslatorApiKey = apiKey;
        public string? GetMicrosoftTranslatorRegion() => MicrosoftTranslatorRegion;
        public void SaveMicrosoftTranslatorRegion(string region) => MicrosoftTranslatorRegion = region;
        public string? GetSubtitleModelKey() => SubtitleModelKey;
        public void SaveSubtitleModelKey(string modelKey) => SubtitleModelKey = modelKey;
        public string? GetTranslationModelKey() => TranslationModelKey;
        public void SaveTranslationModelKey(string modelKey) => TranslationModelKey = modelKey;
        public void ClearTranslationModelKey() => TranslationModelKey = null;
        public bool GetAutoTranslateEnabled() => AutoTranslateEnabled;
        public void SaveAutoTranslateEnabled(bool enabled) => AutoTranslateEnabled = enabled;
        public string? GetLlamaCppServerPath() => LlamaCppServerPath;
        public void SaveLlamaCppServerPath(string path) => LlamaCppServerPath = path;
        public string? GetLlamaCppRuntimeVersion() => LlamaCppRuntimeVersion;
        public void SaveLlamaCppRuntimeVersion(string version) => LlamaCppRuntimeVersion = version;
        public string? GetLlamaCppRuntimeSource() => LlamaCppRuntimeSource;
        public void SaveLlamaCppRuntimeSource(string source) => LlamaCppRuntimeSource = source;
    }

    private sealed class FakeProviderAvailabilityService : IProviderAvailabilityService
    {
        public Dictionary<TranslationProvider, bool> ConfiguredProviders { get; init; } = [];
        public string? ResolvedTranscriptionModelKey { get; init; }
        public string? ResolvedTranslationModelKey { get; init; }
        public string? ResolvedLlamaCppServerPath { get; init; }

        public string ResolvePersistedTranscriptionModelKey(string? modelKey)
            => ResolvedTranscriptionModelKey ?? SubtitleWorkflowCatalog.CanonicalizeTranscriptionModelKey(modelKey);

        public string? ResolvePersistedTranslationModelKey(string? modelKey)
            => ResolvedTranslationModelKey ?? modelKey;

        public bool IsTranslationProviderConfigured(TranslationProvider provider)
            => ConfiguredProviders.TryGetValue(provider, out var configured) && configured;

        public string? ResolveLlamaCppServerPath() => ResolvedLlamaCppServerPath;
    }

    private sealed class FakeQueueCommands : IQueueCommands
    {
        public int MoveNextCalls { get; private set; }

        public ShellQueueMediaResult EnqueueFiles(IEnumerable<string> files, bool autoplay) => new();
        public ShellQueueMediaResult EnqueueFolder(string folderPath, bool autoplay) => new();
        public ShellQueueMediaResult EnqueueDroppedItems(IEnumerable<string> files, IEnumerable<string> folders) => new();
        public ShellQueueMediaResult PlayNow(string path) => new();
        public ShellQueueMediaResult PlayNext(string path) => new();
        public ShellQueueMediaResult AddToQueue(IEnumerable<string> files) => new();
        public ShellQueueMediaResult AddDroppedItemsToQueue(IEnumerable<string> files, IEnumerable<string> folders) => new();
        public ShellPlaylistItem? MovePrevious() => new() { Path = "previous.mp4", DisplayName = "previous.mp4" };
        public ShellPlaylistItem? MoveNext()
        {
            MoveNextCalls++;
            return new ShellPlaylistItem { Path = "next.mp4", DisplayName = "next.mp4" };
        }
        public void RemoveQueueItemAt(int index) { }
        public void ClearQueue() { }
    }

    private sealed class FakeShortcutProfileService : IShortcutProfileService
    {
        public event Action<ShortcutProfileSnapshot>? SnapshotChanged;

        public ShortcutProfileSnapshot Current { get; private set; } = new();

        public ShortcutProfileValidationResult ValidateProfile(ShellShortcutProfile profile)
            => new(true, [], [], []);

        public ShortcutProfileNormalizationResult NormalizeProfile(ShellShortcutProfile profile)
            => new(profile, [], [], []);

        public ShortcutProfileSnapshot ApplyShortcutProfileChange(ShellShortcutProfile profile)
        {
            Current = new ShortcutProfileSnapshot { Profile = profile };
            SnapshotChanged?.Invoke(Current);
            return Current;
        }
    }

    private sealed class FakeShortcutPlaybackCommands : IShellPlaybackCommands
    {
        public ShellPlaybackStateSnapshot CurrentPlaybackSnapshot { get; set; } = new();
        public int PlayCalls { get; private set; }
        public double LastPlaybackRate { get; private set; } = 1.0;

        public Task<bool> LoadPlaybackItemAsync(ShellPlaylistItem? item, ShellLoadMediaOptions options, CancellationToken cancellationToken) => Task.FromResult(item is not null);
        public Task<ShellPlaybackOpenResult> HandleMediaOpenedAsync(ShellPlaybackStateSnapshot snapshot, ShellPreferencesSnapshot preferences, CancellationToken cancellationToken = default) => Task.FromResult(new ShellPlaybackOpenResult());
        public ShellMediaEndedResult HandleMediaEnded(bool resumeEnabled) => new();
        public Task PlayAsync(CancellationToken cancellationToken = default)
        {
            PlayCalls++;
            CurrentPlaybackSnapshot = CurrentPlaybackSnapshot with { IsPaused = false };
            return Task.CompletedTask;
        }
        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            CurrentPlaybackSnapshot = CurrentPlaybackSnapshot with { IsPaused = true };
            return Task.CompletedTask;
        }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekRelativeAsync(TimeSpan delta, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StepFrameAsync(bool forward, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplyAudioPreferencesAsync(double volume, bool muted, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplyPlaybackDefaultsAsync(ShellPlaybackDefaultsChange change, CancellationToken cancellationToken = default)
        {
            LastPlaybackRate = change.PlaybackRate;
            return Task.CompletedTask;
        }
        public Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken = default)
        {
            LastPlaybackRate = speed;
            return Task.CompletedTask;
        }
        public Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ShellSubtitleTrackSelectionResult> SelectEmbeddedSubtitleTrackAsync(string? currentPath, SubtitlePipelineSource currentSubtitleSource, ShellMediaTrack? track, CancellationToken cancellationToken = default) => Task.FromResult(new ShellSubtitleTrackSelectionResult());
        public Task SetAudioDelayAsync(double seconds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAspectRatioAsync(string aspectRatio, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetHardwareDecodingModeAsync(ShellHardwareDecodingMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetResumeTrackingEnabled(bool enabled) { }
        public void ClearResumeHistory() { }
        public void FlushResumeTracking(bool forceRemoveCompleted = false) { }
        public Task<ShellWorkflowTransitionResult> PrepareForTranscriptionRefreshAsync(SubtitleWorkflowSnapshot snapshot, ShellPlaybackStateSnapshot playbackState, CancellationToken cancellationToken = default) => Task.FromResult(new ShellWorkflowTransitionResult());
        public Task<ShellWorkflowTransitionResult> EvaluateCaptionStartupGateAsync(SubtitleWorkflowSnapshot snapshot, ShellPlaybackStateSnapshot playbackState, CancellationToken cancellationToken = default) => Task.FromResult(new ShellWorkflowTransitionResult());
    }

    private sealed class FakeTranscriptionProvider : ITranscriptionProvider
    {
        private readonly bool _shouldFail;
        private readonly List<string> _calls;

        public FakeTranscriptionProvider(string id, bool shouldFail, List<string> calls)
        {
            Id = id;
            _shouldFail = shouldFail;
            _calls = calls;
        }

        public string Id { get; }

        public TranscriptionProvider Provider => TranscriptionProvider.Local;

        public bool CanHandle(TranscriptionModelSelection selection) => true;

        public bool IsAvailable(TranscriptionModelSelection selection, ProviderAvailabilityContext context) => true;

        public Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(TranscriptionRequest request, ProviderAvailabilityContext context, CancellationToken cancellationToken)
        {
            _calls.Add(Id);
            if (_shouldFail)
            {
                throw new InvalidOperationException("boom");
            }

            IReadOnlyList<SubtitleCue> cues =
            [
                new SubtitleCue
                {
                    Start = TimeSpan.Zero,
                    End = TimeSpan.FromSeconds(1),
                    SourceText = Id,
                    SourceLanguage = "en"
                }
            ];
            return Task.FromResult(cues);
        }
    }

    private sealed class FakeTranslationProvider : ITranslationProvider
    {
        public int CallCount { get; private set; }

        public TranslationProvider Provider => TranslationProvider.DeepL;

        public bool IsConfigured(ProviderAvailabilityContext context) => true;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(TranslationRequest request, ProviderAvailabilityContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            IReadOnlyList<string> translated = ["HELLO", "BYE"];
            return Task.FromResult(translated);
        }
    }

    private sealed class FakeSubtitleSourceResolver : ISubtitleSourceResolver
    {
        public int ExternalCalls { get; private set; }
        public int EmbeddedCalls { get; private set; }

        public Task<IReadOnlyList<SubtitleCue>> LoadExternalSubtitleCuesAsync(string path, Action<RuntimeInstallProgress>? onRuntimeProgress, Action<string>? onStatus, CancellationToken cancellationToken)
        {
            ExternalCalls++;
            return Task.FromResult<IReadOnlyList<SubtitleCue>>([]);
        }

        public Task<IReadOnlyList<SubtitleCue>> ExtractEmbeddedSubtitleCuesAsync(string videoPath, MediaTrackInfo track, Action<RuntimeInstallProgress>? onRuntimeProgress, Action<string>? onStatus, CancellationToken cancellationToken)
        {
            EmbeddedCalls++;
            return Task.FromResult<IReadOnlyList<SubtitleCue>>([]);
        }
    }

    private sealed class FakeLocalModelRuntime : ILocalModelRuntime
    {
        public string RuntimeId => "test";

        public string? ResolveExecutablePath(ProviderAvailabilityContext context) => null;
    }

    private sealed class FakeCaptionGenerator : ICaptionGenerator
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<SubtitleCue>> GenerateCaptionsAsync(string videoPath, TranscriptionModelSelection selection, string? languageHint, Action<TranscriptChunk>? onFinal, Action<ModelTransferProgress>? onProgress, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<SubtitleCue>>([]);
        }
    }

    private sealed class FakeSubtitleTranslator : ISubtitleTranslator
    {
        public event Action<LocalTranslationRuntimeStatus>? RuntimeStatusChanged;

        public int CallCount { get; private set; }

        public Task WarmupAsync(TranslationModelSelection selection, CancellationToken cancellationToken)
        {
            RuntimeStatusChanged?.Invoke(new LocalTranslationRuntimeStatus
            {
                Stage = "ready",
                Message = "ready"
            });
            return Task.CompletedTask;
        }

        public Task<string> TranslateAsync(TranslationModelSelection selection, string text, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(text);
        }

        public Task<IReadOnlyList<string>> TranslateBatchAsync(TranslationModelSelection selection, IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(texts);
        }
    }

    private sealed class FakeAiCredentialCoordinator : IAiCredentialCoordinator
    {
        public int OpenAiCalls { get; private set; }
        public int TranslationCalls { get; private set; }

        public Task<bool> EnsureOpenAiApiKeyAsync(CancellationToken cancellationToken)
        {
            OpenAiCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> EnsureTranslationProviderCredentialsAsync(TranslationProvider provider, CancellationToken cancellationToken)
        {
            TranslationCalls++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeRuntimeProvisioner : IRuntimeProvisioner
    {
        public int LlamaCalls { get; private set; }
        public int FfmpegCalls { get; private set; }

        public Task<string> EnsureLlamaCppAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        {
            LlamaCalls++;
            return Task.FromResult("llama");
        }

        public Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        {
            FfmpegCalls++;
            return Task.FromResult("ffmpeg");
        }

        public Task<bool> EnsureLlamaCppRuntimeReadyAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        {
            LlamaCalls++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeShellPlaybackBackend : IPlaybackBackend
    {
        public List<string> LoadedPaths { get; } = [];
        public List<int?> SubtitleTrackSelections { get; } = [];
        public List<double> VolumeHistory { get; } = [];
        public List<bool> MuteHistory { get; } = [];
        public List<double> PlaybackRateHistory { get; } = [];
        public List<double> SubtitleDelayHistory { get; } = [];
        public List<double> AudioDelayHistory { get; } = [];
        public int PauseCallCount { get; private set; }
        public int PlayCallCount { get; private set; }
        public int SubtitleTrackSetCallCount { get; private set; }
        public TimeSpan? LastSeekPosition { get; private set; }
        public int? LastSubtitleTrackId { get; private set; }
        public double LastVolume { get; private set; }
        public bool LastMuted { get; private set; }
        public double LastPlaybackRate { get; private set; } = 1.0;
        public double LastSubtitleDelay { get; private set; }
        public double LastAudioDelay { get; private set; }

        public event Action<PlaybackBackendState>? StateChanged;
        public event Action<IReadOnlyList<MediaTrackInfo>>? TracksChanged;
        public event Action? MediaOpened;
        public event Action? MediaEnded;
        public event Action<string>? MediaFailed;
        public event Action<RuntimeInstallProgress>? RuntimeInstallProgress;

        private readonly FakePlaybackClock _clock = new();
        public IPlaybackClock Clock => _clock;
        public PlaybackBackendState State { get; private set; } = new();
        public IReadOnlyList<MediaTrackInfo> CurrentTracks { get; private set; } = [];
        public BabelPlayer.Core.HardwareDecodingMode HardwareDecodingMode { get; private set; }
        public ClockSnapshot ClockSnapshot
        {
            set => _clock.Set(value);
        }

        public Task InitializeAsync(nint hostHandle, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task LoadAsync(string path, CancellationToken cancellationToken)
        {
            LoadedPaths.Add(path);
            State = State with { Path = path };
            StateChanged?.Invoke(State);
            MediaOpened?.Invoke();
            return Task.CompletedTask;
        }

        public void SetState(PlaybackBackendState state)
        {
            State = state;
            StateChanged?.Invoke(State);
        }

        public Task PlayAsync(CancellationToken cancellationToken)
        {
            PlayCallCount++;
            _clock.Set(_clock.Current with { IsPaused = false });
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken)
        {
            PauseCallCount++;
            _clock.Set(_clock.Current with { IsPaused = true });
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
        {
            LastSeekPosition = position;
            _clock.Set(_clock.Current with { Position = position });
            return Task.CompletedTask;
        }
        public Task SeekRelativeAsync(TimeSpan delta, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken)
        {
            LastPlaybackRate = speed;
            PlaybackRateHistory.Add(speed);
            return Task.CompletedTask;
        }
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken)
        {
            LastVolume = volume;
            VolumeHistory.Add(volume);
            State = State with { Volume = volume };
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }
        public Task SetMuteAsync(bool muted, CancellationToken cancellationToken)
        {
            LastMuted = muted;
            MuteHistory.Add(muted);
            State = State with { IsMuted = muted };
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }
        public Task StepFrameAsync(bool forward, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken)
        {
            SubtitleTrackSetCallCount++;
            LastSubtitleTrackId = trackId;
            SubtitleTrackSelections.Add(trackId);
            return Task.CompletedTask;
        }
        public Task SetAudioDelayAsync(double seconds, CancellationToken cancellationToken)
        {
            LastAudioDelay = seconds;
            AudioDelayHistory.Add(seconds);
            return Task.CompletedTask;
        }
        public Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken)
        {
            LastSubtitleDelay = seconds;
            SubtitleDelayHistory.Add(seconds);
            return Task.CompletedTask;
        }
        public Task SetAspectRatioAsync(string aspectRatio, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetHardwareDecodingModeAsync(BabelPlayer.Core.HardwareDecodingMode mode, CancellationToken cancellationToken)
        {
            HardwareDecodingMode = mode;
            return Task.CompletedTask;
        }

        public Task SetZoomAsync(double zoom, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetPanAsync(double x, double y, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ScreenshotAsync(string outputPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class FakePlaybackClock : IPlaybackClock
        {
            public event Action<ClockSnapshot>? Changed;

            public ClockSnapshot Current { get; private set; } = new(TimeSpan.Zero, TimeSpan.Zero, 1.0, true, false, DateTimeOffset.UtcNow);

            public void Set(ClockSnapshot snapshot)
            {
                Current = snapshot;
                Changed?.Invoke(snapshot);
            }
        }
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, int timeoutMilliseconds = 1000)
    {
        var start = DateTimeOffset.UtcNow;
        while (!predicate())
        {
            if ((DateTimeOffset.UtcNow - start).TotalMilliseconds > timeoutMilliseconds)
            {
                throw new TimeoutException("Timed out waiting for condition.");
            }

            await Task.Delay(20);
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
