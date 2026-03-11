using BabelPlayer.App;
using BabelPlayer.Core;
using Whisper.net.Ggml;

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
        Assert.Equal(0.42, projection.Transport.Volume);
        Assert.Equal("d3d11va", projection.Transport.ActiveHardwareDecoder);
        Assert.Equal(2, projection.SelectedTracks.Tracks.Count);
        Assert.Equal(1, projection.SelectedTracks.ActiveAudioTrackId);
        Assert.Equal(3, projection.SelectedTracks.ActiveSubtitleTrackId);
        Assert.Equal("Hola", projection.Subtitle.SourceText);
        Assert.Equal("Hello", projection.Subtitle.TranslationText);
        Assert.True(projection.Subtitle.IsTranslationEnabled);
        Assert.True(projection.Subtitle.IsAutoTranslateEnabled);

        ((MediaTrackInfo[])projection.SelectedTracks.Tracks)[0] = new MediaTrackInfo
        {
            Id = 1,
            Kind = MediaTrackKind.Audio,
            Title = "mutated",
            Language = "ja",
            IsSelected = true
        };

        Assert.Equal("Japanese", coordinator.Snapshot.Streams.Tracks[0].Title);
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
            new TranslationProviderRegistry([adapter]));

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
        var providerAvailabilityService = new ProviderAvailabilityService(credentialFacade, _ => null);
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
            new ProviderAvailabilityService(credentialFacade, _ => null));

        workflowStateStore.Update(state => state with
        {
            SelectedTranslationModelKey = "cloud:deepl",
            IsTranslationEnabled = true
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
            IsTranslationEnabled = true,
            AutoTranslateEnabled = true,
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
        Assert.Equal("local:small", snapshot.SelectedTranscriptionModelKey);
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
        var snapshot = new PlaybackStateSnapshot
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

            var playlist = new PlaylistController();
            var session = new PlaybackSessionController(playlist);
            var backend = new FakeShellPlaybackBackend();
            using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
            using var shell = new ShellController(
                playlist,
                session,
                backend,
                workflow,
                new LibraryBrowserService(),
                new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

            var queueResult = shell.EnqueueFiles([firstPath, secondPath], autoplay: true);
            var loaded = await shell.LoadPlaylistItemAsync(
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
            Assert.Equal(secondPath, playlist.CurrentItem?.Path);
            Assert.Equal(secondPath, ended.NextItem?.Path);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ShellController_ExposesAndMutatesPlaylistStateWithoutWindowAccess()
    {
        var playlist = new PlaylistController();
        var session = new PlaybackSessionController(playlist);
        var backend = new FakeShellPlaybackBackend();
        using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
        using var shell = new ShellController(
            playlist,
            session,
            backend,
            workflow,
            new LibraryBrowserService(),
            new ResumePlaybackService(initialEntries: [], persistEntries: _ => { }));

        shell.EnqueueFiles(["first.mp4", "second.mp4"], autoplay: false);
        shell.RemovePlaylistItemAt(0);

        Assert.Single(shell.PlaylistItems);
        Assert.Equal("second.mp4", shell.PlaylistItems[0].Path);
        Assert.Equal(0, shell.CurrentPlaylistIndex);
        Assert.Equal("second.mp4", shell.CurrentPlaylistItem?.Path);

        shell.ClearPlaylist();

        Assert.Empty(shell.PlaylistItems);
        Assert.Equal(-1, shell.CurrentPlaylistIndex);
        Assert.Null(shell.CurrentPlaylistItem);
    }

    [Fact]
    public async Task ShellController_HandleMediaOpenedAppliesResumeThroughBackend()
    {
        var playlist = new PlaylistController();
        playlist.EnqueueFiles(["C:\\Media\\movie.mp4"]);
        var session = new PlaybackSessionController(playlist);
        var backend = new FakeShellPlaybackBackend();
        using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
        var resumeEntry = new PlaybackResumeEntry
        {
            Path = "C:\\Media\\movie.mp4",
            PositionSeconds = 125,
            DurationSeconds = 3600,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        using var shell = new ShellController(
            playlist,
            session,
            backend,
            workflow,
            new LibraryBrowserService(),
            new ResumePlaybackService(initialEntries: [resumeEntry], persistEntries: _ => { }));

        var result = await shell.HandleMediaOpenedAsync(
            new PlaybackStateSnapshot
            {
                Path = "C:\\Media\\movie.mp4",
                Duration = TimeSpan.FromMinutes(60)
            },
            resumeEnabled: true);

        Assert.Equal(TimeSpan.FromSeconds(125), result.ResumePosition);
        Assert.Equal(TimeSpan.FromSeconds(125), backend.LastSeekPosition);
    }

    [Fact]
    public async Task ShellController_CaptionStartupGatePausesAndResumesThroughBackend()
    {
        var playlist = new PlaylistController();
        var session = new PlaybackSessionController(playlist);
        var backend = new FakeShellPlaybackBackend
        {
            ClockSnapshot = new ClockSnapshot(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), 1.0, false, true, DateTimeOffset.UtcNow)
        };
        using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
        using var shell = new ShellController(
            playlist,
            session,
            backend,
            workflow,
            new LibraryBrowserService(),
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
                    new SubtitleCue
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
            new ProviderAvailabilityService(credentialFacade, _ => null));
        using var projectionAdapter = new SubtitleWorkflowProjectionAdapter(workflowStore, mediaSessionCoordinator.Store);
        using var controller = new SubtitleWorkflowController(service, projectionAdapter, new SubtitlePresentationProjector());

        Assert.Same(service.MediaSessionStore, controller.MediaSessionStore);
        Assert.Equal(controller.Snapshot.CurrentVideoPath, projectionAdapter.Current.CurrentVideoPath);
    }

    [Fact]
    public async Task ShellController_CurrentPlaybackSnapshotReflectsBackendState()
    {
        var playlist = new PlaylistController();
        var session = new PlaybackSessionController(playlist);
        var backend = new FakeShellPlaybackBackend();
        using var workflow = TestWorkflowControllerFactory.Create(new CredentialFacade(new FakeCredentialStore()), environmentVariableReader: _ => null);
        using var shell = new ShellController(
            playlist,
            session,
            backend,
            workflow,
            new LibraryBrowserService(),
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
        public int PauseCallCount { get; private set; }
        public int PlayCallCount { get; private set; }
        public TimeSpan? LastSeekPosition { get; private set; }

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
        public HardwareDecodingMode HardwareDecodingMode { get; private set; }
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
