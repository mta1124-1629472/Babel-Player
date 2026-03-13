using System.Text.Json;
using BabelPlayer.App;
using BabelPlayer.Core;
using ShortcutProfile = BabelPlayer.App.ShellShortcutProfile;
using SubtitleRenderMode = BabelPlayer.App.ShellSubtitleRenderMode;
using SubtitleStyleSettings = BabelPlayer.App.ShellSubtitleStyle;

namespace BabelPlayer.App.Tests;

public sealed class AppLayerTests
{
    [Fact]
    public void PlaybackQueueController_AutoAdvancePromotesQueueAndMovesCurrentToHistory()
    {
        var controller = new PlaybackQueueController();

        controller.PlayNow("first.mp4");
        controller.AddToQueue(["second.mp4", "third.mp4"]);

        Assert.Equal("first.mp4", controller.NowPlayingItem?.Path);

        var next = controller.AdvanceAfterMediaEnded();

        Assert.NotNull(next);
        Assert.Equal("second.mp4", next!.Path);
        Assert.Equal("second.mp4", controller.NowPlayingItem?.Path);
        Assert.Equal("first.mp4", controller.HistoryItems[0].Path);
    }

    [Fact]
    public void PlaybackQueueController_PlayNowDoesNotAppendCurrentItemToQueue()
    {
        var controller = new PlaybackQueueController();

        controller.PlayNow("current.mp4");
        controller.AddToQueue(["future-a.mp4", "future-b.mp4"]);
        controller.PlayNow("replacement.mp4");

        Assert.Equal("replacement.mp4", controller.NowPlayingItem?.Path);
        Assert.Equal(new[] { "future-a.mp4", "future-b.mp4" }, controller.QueueItems.Select(item => item.Path).ToArray());
        Assert.Equal("current.mp4", controller.HistoryItems[0].Path);
    }

    [Fact]
    public void PlaybackQueueController_PlayNextInsertsAtFrontWhilePreservingOrder()
    {
        var controller = new PlaybackQueueController();

        controller.AddToQueue(["later-1.mp4", "later-2.mp4"]);
        controller.PlayNext(["next-1.mp4", "next-2.mp4"]);

        Assert.Equal(
            new[] { "next-1.mp4", "next-2.mp4", "later-1.mp4", "later-2.mp4" },
            controller.QueueItems.Select(item => item.Path).ToArray());
    }

    [Fact]
    public void ShortcutService_FindsConflictsAndNormalizesModifierOrder()
    {
        var service = new ShortcutService();
        var profile = new ShortcutProfile
        {
            Bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["play_pause"] = "Shift+Ctrl+P",
                ["pip"] = "Ctrl+Shift+P",
                ["mute"] = "M"
            }
        };

        var conflicts = service.FindConflicts(profile);

        Assert.Single(conflicts);
        Assert.Equal("Ctrl+Shift+P", conflicts[0].Gesture);
        Assert.Contains(conflicts[0].ExistingAction, new[] { "play_pause", "pip" });
        Assert.Contains(conflicts[0].ConflictingAction, new[] { "play_pause", "pip" });
        Assert.NotEqual(conflicts[0].ExistingAction, conflicts[0].ConflictingAction);
    }

    [Fact]
    public void ShortcutService_SupportedActionsCoverDefaultShortcutProfile()
    {
        var defaultProfile = ShortcutProfile.CreateDefault();
        var supportedActionIds = ShortcutService.SupportedActions
            .Select(action => action.CommandId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(supportedActionIds.Count, ShortcutService.SupportedActions.Count);
        Assert.All(defaultProfile.Bindings.Keys, commandId => Assert.Contains(commandId, supportedActionIds));
    }

    [Fact]
    public void SubtitleWorkflowController_TogglesRenderModesAndPreservesUnchangedStyleValues()
    {
        var controller = TestWorkflowControllerFactory.Create();
        var currentStyle = new SubtitleStyleSettings
        {
            SourceFontSize = 28,
            TranslationFontSize = 30,
            BackgroundOpacity = 0.78
        };

        var toggled = controller.ToggleSource(SubtitleRenderMode.TranslationOnly);
        var updatedStyle = controller.UpdateStyle(currentStyle, sourceFontSize: 32, bottomMargin: 24);

        Assert.Equal(SubtitleRenderMode.Dual, toggled);
        Assert.Equal(32, updatedStyle.SourceFontSize);
        Assert.Equal(30, updatedStyle.TranslationFontSize);
        Assert.Equal(0.78, updatedStyle.BackgroundOpacity);
        Assert.Equal(24, updatedStyle.BottomMargin);
    }

    [Fact]
    public void SettingsModels_DeserializeExistingJsonShape()
    {
        const string settingsJson = """
            {
              "HardwareDecodingMode": 1,
              "SubtitleRenderMode": 3,
              "PinnedRoots": ["C:\\Media"],
              "DefaultPlaybackRate": 1.25,
              "AudioDelaySeconds": 0.15,
              "SubtitleDelaySeconds": -0.25,
              "AspectRatioOverride": "16:9",
              "ShowBrowserPanel": true,
              "ShowPlaylistPanel": false,
              "ResumeEnabled": true,
              "WindowMode": 2
            }
            """;

        const string resumeJson = """
            [
              {
                "Path": "C:\\Media\\sample.mp4",
                "PositionSeconds": 123.45,
                "DurationSeconds": 456.78,
                "UpdatedAt": "2026-03-08T10:30:00Z"
              }
            ]
            """;

        var settings = JsonSerializer.Deserialize<AppPlayerSettings>(settingsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var resumeEntries = JsonSerializer.Deserialize<PlaybackResumeEntry[]>(resumeJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(settings);
        Assert.Equal(BabelPlayer.Core.HardwareDecodingMode.D3D11, settings!.HardwareDecodingMode);
        Assert.Equal(BabelPlayer.Core.SubtitleRenderMode.Dual, settings.SubtitleRenderMode);
        Assert.Single(settings.PinnedRoots);
        Assert.Equal(0.8, settings.VolumeLevel);
        Assert.False(settings.IsMuted);
        Assert.Equal(BabelPlayer.Core.PlaybackWindowMode.PictureInPicture, settings.WindowMode);

        Assert.NotNull(resumeEntries);
        Assert.Single(resumeEntries!);
        Assert.Equal("C:\\Media\\sample.mp4", resumeEntries[0].Path);
        Assert.Equal(123.45, resumeEntries[0].PositionSeconds);
    }

    [Fact]
    public void CredentialFacade_PersistsModelSelectionsThroughStore()
    {
        var store = new FakeCredentialStore();
        var facade = new CredentialFacade(store);

        facade.SaveSubtitleModelKey("whisper-large-v3");
        facade.SaveTranslationModelKey("gpt-4.1-mini");
        facade.SaveAutoTranslateEnabled(true);
        facade.ClearTranslationModelKey();

        Assert.Equal("whisper-large-v3", facade.GetSubtitleModelKey());
        Assert.Null(facade.GetTranslationModelKey());
        Assert.True(facade.GetAutoTranslateEnabled());
    }

    [Fact]
    public async Task SubtitleWorkflowController_FallsBackToLocalTranscriptionWhenOpenAiIsMissing()
    {
        var store = new FakeCredentialStore();
        store.SaveSubtitleModelKey("cloud:gpt-4o-transcribe");
        var facade = new CredentialFacade(store);
        var controller = TestWorkflowControllerFactory.Create(
            facade,
            environmentVariableReader: _ => null);

        await controller.InitializeAsync();

        Assert.Equal("local:tiny-multilingual", controller.Snapshot.SelectedTranscriptionModelKey);
    }

    [Fact]
    public async Task SubtitleWorkflowController_UsesPersistedTranslationModelWhenProviderIsConfigured()
    {
        var store = new FakeCredentialStore();
        store.SaveTranslationModelKey("cloud:deepl");
        store.SaveDeepLApiKey("configured");
        var controller = TestWorkflowControllerFactory.Create(
            new CredentialFacade(store),
            environmentVariableReader: _ => null);

        await controller.InitializeAsync();

        Assert.Equal("cloud:deepl", controller.Snapshot.SelectedTranslationModelKey);
        Assert.Equal("Cloud DeepL API", controller.Snapshot.SelectedTranslationLabel);
    }

    [Fact]
    public async Task SubtitleWorkflowController_ClearsPersistedTranslationModelWhenProviderIsUnavailable()
    {
        var store = new FakeCredentialStore();
        store.SaveTranslationModelKey("cloud:google-translate");
        var controller = TestWorkflowControllerFactory.Create(
            new CredentialFacade(store),
            environmentVariableReader: _ => null);

        await controller.InitializeAsync();

        Assert.Null(controller.Snapshot.SelectedTranslationModelKey);
    }

    [Fact]
    public async Task SubtitleWorkflowController_PromptsForGoogleCredentialsBeforeSelectingModel()
    {
        var store = new FakeCredentialStore();
        var dialogs = new FakeCredentialDialogService();
        dialogs.ApiKeyResponses.Enqueue("google-key");
        var controller = TestWorkflowControllerFactory.Create(
            new CredentialFacade(store),
            dialogs,
            new FakeFilePickerService(),
            new FakeRuntimeBootstrapService(),
            environmentVariableReader: _ => null,
            validateOpenAiApiKeyAsync: (_, _) => Task.CompletedTask,
            validateTranslationProviderAsync: (_, _) => Task.CompletedTask);

        var applied = await controller.SelectTranslationModelAsync("cloud:google-translate");

        Assert.True(applied);
        Assert.Equal("google-key", store.GetGoogleTranslateApiKey());
        Assert.Equal("cloud:google-translate", controller.Snapshot.SelectedTranslationModelKey);
    }

    [Fact]
    public async Task SubtitleWorkflowController_SelectingTranslationModelDoesNotEnableTranslationByItself()
    {
        var store = new FakeCredentialStore();
        store.SaveDeepLApiKey("configured");
        var controller = TestWorkflowControllerFactory.Create(
            new CredentialFacade(store),
            environmentVariableReader: _ => null,
            validateTranslationProviderAsync: (_, _) => Task.CompletedTask);

        var applied = await controller.SelectTranslationModelAsync("cloud:deepl");

        Assert.True(applied);
        Assert.Equal("cloud:deepl", controller.Snapshot.SelectedTranslationModelKey);
        Assert.False(controller.Snapshot.IsTranslationEnabled);
    }

    [Fact]
    public async Task SubtitleWorkflowController_EnablingTranslationWithoutModelKeepsSelectorFlowAvailable()
    {
        var store = new FakeCredentialStore();
        var controller = TestWorkflowControllerFactory.Create(
            new CredentialFacade(store),
            environmentVariableReader: _ => null);

        await controller.SetTranslationEnabledAsync(true);

        Assert.True(controller.Snapshot.IsTranslationEnabled);
        Assert.Null(controller.Snapshot.SelectedTranslationModelKey);
    }

    [Fact]
    public async Task SubtitleWorkflowController_DoesNotPersistLocalTranslationSelectionWhenWarmupFails()
    {
        var store = new FakeCredentialStore();
        var dialogs = new FakeCredentialDialogService
        {
            LlamaChoice = LlamaCppBootstrapChoice.InstallAutomatically
        };
        var runtimeBootstrap = new FakeRuntimeBootstrapService();
        var controller = TestWorkflowControllerFactory.Create(
            new CredentialFacade(store),
            dialogs,
            new FakeFilePickerService(),
            runtimeBootstrap,
            environmentVariableReader: _ => null,
            validateOpenAiApiKeyAsync: (_, _) => Task.CompletedTask,
            validateTranslationProviderAsync: (_, _) => Task.CompletedTask,
            providerAvailabilityService: new FakeProviderAvailabilityService(),
            subtitleTranslator: new ThrowingWarmupSubtitleTranslator(),
            runtimeProvisioner: new FakeSuccessfulRuntimeProvisioner());

        var applied = await controller.SelectTranslationModelAsync("local:hymt-1.8b");

        Assert.False(applied);
        Assert.Null(controller.Snapshot.SelectedTranslationModelKey);
    }

    [Fact]
    public async Task SubtitleWorkflowController_AutoTranslateStaysOffForEnglishSourceLanguageWithoutModel()
    {
        var store = new FakeCredentialStore();
        var controller = TestWorkflowControllerFactory.Create(
            new CredentialFacade(store),
            environmentVariableReader: _ => null);

        await controller.SetAutoTranslateEnabledAsync(true);

        Assert.False(controller.Snapshot.AutoTranslateEnabled);
    }

    [Fact]
    public async Task SubtitleWorkflowController_KeepsEnglishSidecarInSourceLanguageWhenAutoTranslateIsEnabled()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var store = new FakeCredentialStore();
            store.SaveTranslationModelKey("cloud:deepl");
            store.SaveDeepLApiKey("configured");
            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(store),
                environmentVariableReader: _ => null,
                validateTranslationProviderAsync: (_, _) => Task.CompletedTask);

            await controller.InitializeAsync();
            await controller.SetAutoTranslateEnabledAsync(true);

            var videoPath = Path.Combine(directory.FullName, "english.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "english.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:00,000 --> 00:00:02,000
Hello there
""");

            await controller.LoadMediaSubtitlesAsync(videoPath);

            Assert.False(controller.Snapshot.IsTranslationEnabled);
            Assert.Equal("en", controller.Snapshot.CurrentSourceLanguage);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitleWorkflowController_LoadsSidecarWhenPresent()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:00,000 --> 00:00:02,000
Hola
""");

            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(new FakeCredentialStore()),
                environmentVariableReader: _ => null);

            var result = await controller.LoadMediaSubtitlesAsync(videoPath);

            Assert.True(result.UsedSidecar);
            Assert.False(result.UsedGeneratedCaptions);
            Assert.Equal(SubtitlePipelineSource.Sidecar, controller.Snapshot.SubtitleSource);
            Assert.Single(controller.Snapshot.Cues);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitleWorkflowController_BuildsSourceOverlayForEnglishCueWhenTranslationIsOff()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:00,000 --> 00:00:02,000
Hello there
""");

            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(new FakeCredentialStore()),
                mediaSessionCoordinator: mediaSessionCoordinator,
                environmentVariableReader: _ => null);

            await controller.LoadMediaSubtitlesAsync(videoPath);
            mediaSessionCoordinator.ApplyClock(new ClockSnapshot(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(5),
                1.0,
                false,
                true,
                DateTimeOffset.UtcNow));

            var presentation = controller.GetOverlayPresentation(SubtitleRenderMode.Dual);

            Assert.True(presentation.IsVisible);
            Assert.Equal("Hello there", presentation.PrimaryText);
            Assert.Equal(string.Empty, presentation.SecondaryText);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitleWorkflowController_BuildsDualOverlayWhenTranslatedCueDiffersFromSource()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:00,000 --> 00:00:02,000
Hola
""");

            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(new FakeCredentialStore()),
                mediaSessionCoordinator: mediaSessionCoordinator,
                environmentVariableReader: _ => null);

            await controller.LoadMediaSubtitlesAsync(videoPath);
            var transcript = Assert.Single(mediaSessionCoordinator.Snapshot.Transcript.Segments);
            mediaSessionCoordinator.UpsertTranslationSegment(CreateTranslationSegment(transcript, "Hello"));
            mediaSessionCoordinator.ApplyClock(new ClockSnapshot(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(5),
                1.0,
                false,
                true,
                DateTimeOffset.UtcNow));

            var presentation = controller.GetOverlayPresentation(SubtitleRenderMode.Dual);

            Assert.True(presentation.IsVisible);
            Assert.Equal("Hello", presentation.PrimaryText);
            Assert.Equal("Hola", presentation.SecondaryText);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitleWorkflowController_UsesTranslatedFirstPresentationWhenSourceOnlyIsPersisted()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var store = new FakeCredentialStore();
            store.SaveDeepLApiKey("configured");
            var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:00,000 --> 00:00:02,000
Hola
""");

            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(store),
                mediaSessionCoordinator: mediaSessionCoordinator,
                environmentVariableReader: _ => null,
                validateTranslationProviderAsync: (_, _) => Task.CompletedTask);

            await controller.InitializeAsync();
            await controller.LoadMediaSubtitlesAsync(videoPath);
            mediaSessionCoordinator.SetTranslationState(true, false);
            var transcript = Assert.Single(mediaSessionCoordinator.Snapshot.Transcript.Segments);
            mediaSessionCoordinator.UpsertTranslationSegment(CreateTranslationSegment(transcript, "Hello"));
            mediaSessionCoordinator.ApplyClock(new ClockSnapshot(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(5),
                1.0,
                false,
                true,
                DateTimeOffset.UtcNow));

            var effectiveMode = controller.GetEffectiveRenderMode(SubtitleRenderMode.SourceOnly);
            var presentation = controller.GetOverlayPresentation(SubtitleRenderMode.SourceOnly);

            Assert.Equal(SubtitleRenderMode.TranslationOnly, effectiveMode);
            Assert.True(presentation.IsVisible);
            Assert.Equal("Hello", presentation.PrimaryText);
            Assert.Equal(string.Empty, presentation.SecondaryText);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitleWorkflowController_FallsBackToSourceTextWhenTranslatedTextIsNotReady()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var store = new FakeCredentialStore();
            store.SaveDeepLApiKey("configured");
            var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:00,000 --> 00:00:02,000
Hola
""");

            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(store),
                mediaSessionCoordinator: mediaSessionCoordinator,
                environmentVariableReader: _ => null,
                validateTranslationProviderAsync: (_, _) => Task.CompletedTask);

            await controller.InitializeAsync();
            await controller.LoadMediaSubtitlesAsync(videoPath);
            mediaSessionCoordinator.SetTranslationState(true, false);
            mediaSessionCoordinator.ApplyClock(new ClockSnapshot(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(5),
                1.0,
                false,
                true,
                DateTimeOffset.UtcNow));

            var presentation = controller.GetOverlayPresentation(SubtitleRenderMode.TranslationOnly);

            Assert.True(presentation.IsVisible);
            Assert.Equal("Hola", presentation.PrimaryText);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitleWorkflowController_AllowsSourceOnlyOverrideForCurrentVideo()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var store = new FakeCredentialStore();
            store.SaveDeepLApiKey("configured");
            var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:00,000 --> 00:00:02,000
Hola
""");

            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(store),
                mediaSessionCoordinator: mediaSessionCoordinator,
                environmentVariableReader: _ => null,
                validateTranslationProviderAsync: (_, _) => Task.CompletedTask);

            await controller.InitializeAsync();
            await controller.LoadMediaSubtitlesAsync(videoPath);
            mediaSessionCoordinator.SetTranslationState(true, false);
            var transcript = Assert.Single(mediaSessionCoordinator.Snapshot.Transcript.Segments);
            mediaSessionCoordinator.UpsertTranslationSegment(CreateTranslationSegment(transcript, "Hello"));
            mediaSessionCoordinator.ApplyClock(new ClockSnapshot(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(5),
                1.0,
                false,
                true,
                DateTimeOffset.UtcNow));

            var effectiveMode = controller.GetEffectiveRenderMode(SubtitleRenderMode.SourceOnly, sourceOnlyOverrideForCurrentVideo: true);
            var presentation = controller.GetOverlayPresentation(SubtitleRenderMode.SourceOnly, sourceOnlyOverrideForCurrentVideo: true);

            Assert.Equal(SubtitleRenderMode.SourceOnly, effectiveMode);
            Assert.True(presentation.IsVisible);
            Assert.Equal("Hola", presentation.PrimaryText);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitleWorkflowController_PrefersLatestOverlappingCueDuringPlayback()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
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

            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(new FakeCredentialStore()),
                mediaSessionCoordinator: mediaSessionCoordinator,
                environmentVariableReader: _ => null);

            await controller.LoadMediaSubtitlesAsync(videoPath);
            mediaSessionCoordinator.ApplyClock(new ClockSnapshot(
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

    [Fact]
    public async Task SubtitleWorkflowController_ChangingTranscriptionModelPreservesTranslationEnabledState()
    {
        var store = new FakeCredentialStore();
        store.SaveDeepLApiKey("configured");
        var controller = TestWorkflowControllerFactory.Create(
            new CredentialFacade(store),
            environmentVariableReader: _ => null,
            validateTranslationProviderAsync: (_, _) => Task.CompletedTask);

        await controller.InitializeAsync();
        await controller.SelectTranslationModelAsync("cloud:deepl");
        await controller.SetTranslationEnabledAsync(true);
        await controller.SelectTranscriptionModelAsync("local:small");

        Assert.True(controller.Snapshot.IsTranslationEnabled);
        Assert.Equal("cloud:deepl", controller.Snapshot.SelectedTranslationModelKey);
        Assert.Equal("local:small-multilingual", controller.Snapshot.SelectedTranscriptionModelKey);
    }

    [Fact]
    public void SubtitleWorkflowCatalog_ResolvesBaseModelSeparatelyFromTiny()
    {
        var baseModel = SubtitleWorkflowCatalog.GetTranscriptionModel("local:base");
        var tinyModel = SubtitleWorkflowCatalog.GetTranscriptionModel("local:tiny");

        Assert.Equal("local:base-multilingual", baseModel.Key);
        Assert.Equal("Local Base (multilingual)", baseModel.DisplayName);
        Assert.NotEqual(baseModel.Key, tinyModel.Key);
    }

    [Fact]
    public void SubtitleWorkflowCatalog_ExposesMultilingualLocalTranscriptionModels()
    {
        var model = SubtitleWorkflowCatalog.GetTranscriptionModel("local:tiny-multilingual");

        Assert.Equal("local:tiny-multilingual", model.Key);
        Assert.Equal("Local Tiny (multilingual)", model.DisplayName);
        Assert.Equal("tiny", model.LocalModelKey);
    }

    [Fact]
    public async Task SubtitleWorkflowController_ReusesGeneratedCaptionCachePerTranscriptionModel()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            File.WriteAllText(videoPath, string.Empty);
            var transcribeCalls = 0;
            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(new FakeCredentialStore()),
                environmentVariableReader: _ => null,
                transcribeVideoAsync: (path, options, _, _, _) =>
                {
                    transcribeCalls++;
                    IReadOnlyList<SubtitleCue> cues =
                    [
                        new SubtitleCue
                        {
                            Start = TimeSpan.Zero,
                            End = TimeSpan.FromSeconds(1),
                    SourceText = options.LocalModelType == Whisper.net.Ggml.GgmlType.Small ? "small model cue" : "tiny model cue",
                            SourceLanguage = "en"
                        }
                    ];

                    return Task.FromResult(cues);
                });

            await controller.LoadMediaSubtitlesAsync(videoPath);
            await controller.SelectTranscriptionModelAsync("local:small");
            await controller.SelectTranscriptionModelAsync("local:tiny");

            Assert.Equal(2, transcribeCalls);
            Assert.Equal("tiny model cue", controller.Snapshot.Cues[0].SourceText);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitleWorkflowController_ReprocessesCompletedGeneratedCaptionsWhenTranslationIsEnabled()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var store = new FakeCredentialStore();
            store.SaveDeepLApiKey("configured");
            var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var videoPath = Path.Combine(directory.FullName, "generated.mp4");
            File.WriteAllText(videoPath, string.Empty);

            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(store),
                mediaSessionCoordinator: mediaSessionCoordinator,
                environmentVariableReader: _ => null,
                validateTranslationProviderAsync: (_, _) => Task.CompletedTask,
                transcribeVideoAsync: (_, _, _, _, _) =>
                {
                    IReadOnlyList<SubtitleCue> cues =
                    [
                        new SubtitleCue
                        {
                            Start = TimeSpan.Zero,
                            End = TimeSpan.FromSeconds(2),
                            SourceText = "Hello there",
                            SourceLanguage = "en"
                        }
                    ];

                    return Task.FromResult(cues);
                });

            await controller.InitializeAsync();
            await controller.SelectTranslationModelAsync("cloud:deepl");
            var result = await controller.LoadMediaSubtitlesAsync(videoPath);
            Assert.True(result.UsedGeneratedCaptions);
            Assert.Single(controller.CurrentCues);
            mediaSessionCoordinator.ClearTranslations();

            await controller.SetTranslationEnabledAsync(true);
            await Task.Delay(50);

            Assert.True(controller.Snapshot.IsTranslationEnabled);
            Assert.Equal("Hello there", controller.CurrentCues[0].TranslatedText);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitleWorkflowController_CurrentCuesProjectionDoesNotMutateSessionState()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var mediaSessionCoordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:00,000 --> 00:00:02,000
Hola
""");

            var controller = TestWorkflowControllerFactory.Create(
                new CredentialFacade(new FakeCredentialStore()),
                mediaSessionCoordinator: mediaSessionCoordinator,
                environmentVariableReader: _ => null);

            await controller.LoadMediaSubtitlesAsync(videoPath);
            mediaSessionCoordinator.ClearTranslations();

            controller.CurrentCues[0].TranslatedText = "Hello";

            var sessionCue = Assert.Single(mediaSessionCoordinator.Snapshot.Transcript.Segments);
            Assert.Equal("Hola", sessionCue.Text);
            Assert.Empty(mediaSessionCoordinator.Snapshot.Translation.Segments);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static TranslationSegment CreateTranslationSegment(TranscriptSegment transcript, string text, string language = "en")
    {
        return new TranslationSegment
        {
            Id = new TranslationSegmentId($"test:{transcript.Id.Value}:{language}:{text}"),
            SourceSegmentId = transcript.Id,
            Start = transcript.Start,
            End = transcript.End,
            Text = text,
            Language = language,
            Provenance = new SegmentProvenance
            {
                Source = SubtitlePipelineSource.Generated,
                Provider = "tests",
                ModelKey = "tests"
            },
            Revision = SegmentRevision.Initial
        };
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
        private bool _autoTranslateEnabled;

        public string? GetOpenAiApiKey() => Get("openai");
        public void SaveOpenAiApiKey(string apiKey) => Set("openai", apiKey);
        public string? GetGoogleTranslateApiKey() => Get("google");
        public void SaveGoogleTranslateApiKey(string apiKey) => Set("google", apiKey);
        public string? GetDeepLApiKey() => Get("deepl");
        public void SaveDeepLApiKey(string apiKey) => Set("deepl", apiKey);
        public string? GetMicrosoftTranslatorApiKey() => Get("microsoft");
        public void SaveMicrosoftTranslatorApiKey(string apiKey) => Set("microsoft", apiKey);
        public string? GetMicrosoftTranslatorRegion() => Get("region");
        public void SaveMicrosoftTranslatorRegion(string region) => Set("region", region);
        public string? GetSubtitleModelKey() => Get("subtitle-model");
        public void SaveSubtitleModelKey(string modelKey) => Set("subtitle-model", modelKey);
        public string? GetTranslationModelKey() => Get("translation-model");
        public void SaveTranslationModelKey(string modelKey) => Set("translation-model", modelKey);
        public void ClearTranslationModelKey() => Set("translation-model", null);
        public bool GetAutoTranslateEnabled() => _autoTranslateEnabled;
        public void SaveAutoTranslateEnabled(bool enabled) => _autoTranslateEnabled = enabled;
        public string? GetLlamaCppServerPath() => Get("llama-server");
        public void SaveLlamaCppServerPath(string path) => Set("llama-server", path);
        public string? GetLlamaCppRuntimeVersion() => Get("llama-version");
        public void SaveLlamaCppRuntimeVersion(string version) => Set("llama-version", version);
        public string? GetLlamaCppRuntimeSource() => Get("llama-source");
        public void SaveLlamaCppRuntimeSource(string source) => Set("llama-source", source);

        private string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        private void Set(string key, string? value) => _values[key] = value;
    }

    private sealed class FakeCredentialDialogService : ICredentialDialogService
    {
        public Queue<string?> ApiKeyResponses { get; } = new();
        public Queue<(string ApiKey, string Region)?> ApiKeyWithRegionResponses { get; } = new();
        public LlamaCppBootstrapChoice LlamaChoice { get; set; } = LlamaCppBootstrapChoice.Cancel;

        public Task<string?> PromptForApiKeyAsync(string title, string message, string submitButtonText, CancellationToken cancellationToken = default)
            => Task.FromResult(ApiKeyResponses.Count > 0 ? ApiKeyResponses.Dequeue() : null);

        public Task<(string ApiKey, string Region)?> PromptForApiKeyWithRegionAsync(string title, string message, string submitButtonText, CancellationToken cancellationToken = default)
            => Task.FromResult(ApiKeyWithRegionResponses.Count > 0 ? ApiKeyWithRegionResponses.Dequeue() : null);

        public Task<LlamaCppBootstrapChoice> PromptForLlamaCppBootstrapChoiceAsync(string title, string message, CancellationToken cancellationToken = default)
            => Task.FromResult(LlamaChoice);

        public Task<ShellShortcutProfile?> EditShortcutsAsync(ShellShortcutProfile currentProfile, CancellationToken cancellationToken = default)
            => Task.FromResult<ShellShortcutProfile?>(currentProfile);
    }

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public Task<IReadOnlyList<string>> PickMediaFilesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> PickSubtitleFileAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> PickExecutableAsync(string title, string filterDescription, IReadOnlyList<string> extensions, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("C:\\Tools\\llama-server.exe");

        public Task<string?> PickSaveFileAsync(string suggestedName, string fileTypeDescription, IReadOnlyList<string> extensions, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeRuntimeBootstrapService : IRuntimeBootstrapService
    {
        public bool EnsureLlamaCalled { get; private set; }

        public Task<string> EnsureMpvAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
            => Task.FromResult("C:\\Tools\\mpv.exe");

        public Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
            => Task.FromResult("C:\\Tools\\ffmpeg.exe");

        public Task<string> EnsureLlamaCppAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        {
            EnsureLlamaCalled = true;
            onProgress?.Invoke(new RuntimeInstallProgress { Stage = "ready" });
            return Task.FromResult("C:\\Tools\\llama-server.exe");
        }
    }

    private sealed class FakeProviderAvailabilityService : IProviderAvailabilityService
    {
        public string ResolvePersistedTranscriptionModelKey(string? modelKey)
            => SubtitleWorkflowCatalog.GetTranscriptionModel(modelKey).Key;

        public string? ResolvePersistedTranslationModelKey(string? modelKey)
            => modelKey;

        public bool IsTranslationProviderConfigured(TranslationProvider provider)
            => false;

        public string? ResolveLlamaCppServerPath()
            => null;
    }

    private sealed class ThrowingWarmupSubtitleTranslator : ISubtitleTranslator
    {
        public event Action<LocalTranslationRuntimeStatus>? RuntimeStatusChanged;

        public Task WarmupAsync(TranslationModelSelection selection, CancellationToken cancellationToken)
            => Task.FromException(new InvalidOperationException("warmup failed"));

        public Task<string> TranslateAsync(TranslationModelSelection selection, string text, CancellationToken cancellationToken)
            => Task.FromResult(text);

        public Task<IReadOnlyList<string>> TranslateBatchAsync(TranslationModelSelection selection, IReadOnlyList<string> texts, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(texts.ToArray());
    }

    private sealed class FakeSuccessfulRuntimeProvisioner : IRuntimeProvisioner
    {
        public Task<string> EnsureLlamaCppAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
            => Task.FromResult("llama");

        public Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
            => Task.FromResult("ffmpeg");

        public Task<bool> EnsureLlamaCppRuntimeReadyAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
