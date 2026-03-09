using System.Text.Json;
using BabelPlayer.App;
using BabelPlayer.Core;

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

        private string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        private void Set(string key, string? value) => _values[key] = value;
    }
}
