using System.Text.Json;
using BabelPlayer.App;
using BabelPlayer.Core;
using Whisper.net.Ggml;

namespace BabelPlayer.App.Tests;

public sealed class AppLayerTests
{
    [Fact]
    public void PlaylistController_AutoAdvanceMovesToNextItem()
    {
        var controller = new PlaylistController();

        controller.EnqueueFiles(["first.mp4", "second.mp4", "third.mp4"]);

        Assert.Equal(0, controller.CurrentIndex);
        Assert.Equal("first.mp4", controller.CurrentItem?.Path);

        var next = controller.AdvanceAfterMediaEnded();

        Assert.NotNull(next);
        Assert.Equal("second.mp4", next!.Path);
        Assert.Equal(1, controller.CurrentIndex);
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
        var controller = new SubtitleWorkflowController();
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
        Assert.Equal(HardwareDecodingMode.D3D11, settings!.HardwareDecodingMode);
        Assert.Equal(SubtitleRenderMode.Dual, settings.SubtitleRenderMode);
        Assert.Single(settings.PinnedRoots);
        Assert.Equal(0.8, settings.VolumeLevel);
        Assert.False(settings.IsMuted);
        Assert.Equal(PlaybackWindowMode.PictureInPicture, settings.WindowMode);

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
        var controller = new SubtitleWorkflowController(
            facade,
            environmentVariableReader: _ => null);

        await controller.InitializeAsync();

        Assert.Equal("local:tiny", controller.Snapshot.SelectedTranscriptionModelKey);
    }

    [Fact]
    public async Task SubtitleWorkflowController_UsesPersistedTranslationModelWhenProviderIsConfigured()
    {
        var store = new FakeCredentialStore();
        store.SaveTranslationModelKey("cloud:deepl");
        store.SaveDeepLApiKey("configured");
        var controller = new SubtitleWorkflowController(
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
        var controller = new SubtitleWorkflowController(
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
        var controller = new SubtitleWorkflowController(
            new CredentialFacade(store),
            dialogs,
            new FakeFilePickerService(),
            new FakeRuntimeBootstrapService(),
            _ => null,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

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
        var controller = new SubtitleWorkflowController(
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
        var controller = new SubtitleWorkflowController(
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
        var controller = new SubtitleWorkflowController(
            new CredentialFacade(store),
            dialogs,
            new FakeFilePickerService(),
            runtimeBootstrap,
            _ => null,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

        var applied = await controller.SelectTranslationModelAsync("local:hymt-1.8b");

        Assert.False(applied);
        Assert.Null(controller.Snapshot.SelectedTranslationModelKey);
    }

    [Fact]
    public async Task SubtitleWorkflowController_AutoTranslateStaysOffForEnglishSourceLanguageWithoutModel()
    {
        var store = new FakeCredentialStore();
        var controller = new SubtitleWorkflowController(
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
            var controller = new SubtitleWorkflowController(
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

            var controller = new SubtitleWorkflowController(
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
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:00,000 --> 00:00:02,000
Hello there
""");

            var controller = new SubtitleWorkflowController(
                new CredentialFacade(new FakeCredentialStore()),
                environmentVariableReader: _ => null);

            await controller.LoadMediaSubtitlesAsync(videoPath);
            controller.UpdatePlaybackPosition(TimeSpan.FromSeconds(1));

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
            var videoPath = Path.Combine(directory.FullName, "sample.mp4");
            var sidecarPath = Path.Combine(directory.FullName, "sample.srt");
            File.WriteAllText(videoPath, string.Empty);
            File.WriteAllText(sidecarPath, """
1
00:00:00,000 --> 00:00:02,000
Hola
""");

            var controller = new SubtitleWorkflowController(
                new CredentialFacade(new FakeCredentialStore()),
                environmentVariableReader: _ => null);

            await controller.LoadMediaSubtitlesAsync(videoPath);
            controller.CurrentCues[0].TranslatedText = "Hello";
            controller.UpdatePlaybackPosition(TimeSpan.FromSeconds(1));

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
    public async Task SubtitleWorkflowController_PrefersLatestOverlappingCueDuringPlayback()
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

            var controller = new SubtitleWorkflowController(
                new CredentialFacade(new FakeCredentialStore()),
                environmentVariableReader: _ => null);

            await controller.LoadMediaSubtitlesAsync(videoPath);
            controller.UpdatePlaybackPosition(TimeSpan.FromSeconds(4));

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
        var controller = new SubtitleWorkflowController(
            new CredentialFacade(store),
            environmentVariableReader: _ => null,
            validateTranslationProviderAsync: (_, _) => Task.CompletedTask);

        await controller.InitializeAsync();
        await controller.SelectTranslationModelAsync("cloud:deepl");
        await controller.SetTranslationEnabledAsync(true);
        await controller.SelectTranscriptionModelAsync("local:small");

        Assert.True(controller.Snapshot.IsTranslationEnabled);
        Assert.Equal("cloud:deepl", controller.Snapshot.SelectedTranslationModelKey);
        Assert.Equal("local:small", controller.Snapshot.SelectedTranscriptionModelKey);
    }

    [Fact]
    public void SubtitleWorkflowCatalog_ResolvesBaseModelSeparatelyFromTiny()
    {
        var baseModel = SubtitleWorkflowCatalog.GetTranscriptionModel("local:base");
        var tinyModel = SubtitleWorkflowCatalog.GetTranscriptionModel("local:tiny");

        Assert.Equal("local:base", baseModel.Key);
        Assert.Equal("Local Base.en", baseModel.DisplayName);
        Assert.NotEqual(baseModel.Key, tinyModel.Key);
    }

    [Fact]
    public void SubtitleWorkflowCatalog_ExposesMultilingualLocalTranscriptionModels()
    {
        var model = SubtitleWorkflowCatalog.GetTranscriptionModel("local:tiny-multilingual");

        Assert.Equal("local:tiny-multilingual", model.Key);
        Assert.Equal("Local Tiny (multilingual)", model.DisplayName);
        Assert.Equal(GgmlType.Tiny, model.LocalModelType);
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
            var controller = new SubtitleWorkflowController(
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
                            SourceText = options.LocalModelType == GgmlType.SmallEn ? "small model cue" : "tiny model cue",
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
            var videoPath = Path.Combine(directory.FullName, "generated.mp4");
            File.WriteAllText(videoPath, string.Empty);

            var controller = new SubtitleWorkflowController(
                new CredentialFacade(store),
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
            controller.CurrentCues[0].TranslatedText = null;

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

        public Task<ShortcutProfile?> EditShortcutsAsync(ShortcutProfile currentProfile, CancellationToken cancellationToken = default)
            => Task.FromResult<ShortcutProfile?>(currentProfile);
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
}
