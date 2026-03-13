using System.Runtime.Serialization;
using BabelPlayer.App;
using BabelPlayer.Core;
using BabelPlayer.Infrastructure;
using BabelPlayer.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace BabelPlayer.App.Tests;

#pragma warning disable SYSLIB0050
#pragma warning disable CS0067

public sealed class StageCoordinatorAndProviderCompositionTests
{
    [Fact]
    public void StageCoordinator_FullscreenOverlayVisibilityChangesSubtitleOffset()
    {
        var windowModeService = new FakeWindowModeService { CurrentModeValue = ShellPlaybackWindowMode.Fullscreen };
        var videoPresenter = new FakeVideoPresenter(new RectInt32(10, 20, 640, 360));
        var subtitlePresenter = new FakeSubtitlePresenter();
        var overlayWindow = new FakeFullscreenOverlayWindow();
        var coordinator = new StageCoordinator(
            CreateUninitialized<Border>(),
            windowModeService,
            videoPresenter,
            subtitlePresenter,
            () => overlayWindow,
            new FakeStageOverlayTimer());

        coordinator.PresentSubtitles(
            new BabelPlayer.WinUI.SubtitlePresentationModel
            {
                IsVisible = true,
                PrimaryText = "Hello"
            },
            new ShellSubtitleStyle { BottomMargin = 10 },
            hasLoadedMedia: true);

        Assert.Equal(78, subtitlePresenter.LastBottomOffset);

        coordinator.HandleWindowModeChanged(ShellPlaybackWindowMode.Fullscreen);

        Assert.True(overlayWindow.IsOverlayVisible);
        Assert.Equal(258, subtitlePresenter.LastBottomOffset);
    }

    [Fact]
    public void StageCoordinator_InactiveWindowHidesSubtitlePresentation()
    {
        var coordinator = CreateStageCoordinator(out _, out var subtitlePresenter, out _);

        coordinator.PresentSubtitles(
            new BabelPlayer.WinUI.SubtitlePresentationModel
            {
                IsVisible = true,
                PrimaryText = "Hello"
            },
            new ShellSubtitleStyle(),
            hasLoadedMedia: true);

        coordinator.HandleWindowActivationChanged(false);

        Assert.True(subtitlePresenter.HideCallCount > 0);
    }

    [Fact]
    public void StageCoordinator_ModalSuppressionHidesAndRestoresPresentation()
    {
        var coordinator = CreateStageCoordinator(out _, out var subtitlePresenter, out _);

        coordinator.PresentSubtitles(
            new BabelPlayer.WinUI.SubtitlePresentationModel
            {
                IsVisible = true,
                PrimaryText = "Hello"
            },
            new ShellSubtitleStyle(),
            hasLoadedMedia: true);

        using (coordinator.SuppressModalUi())
        {
            Assert.True(subtitlePresenter.HideCallCount > 0);
        }

        Assert.True(subtitlePresenter.PresentCallCount >= 2);
    }

    [Fact]
    public void StageCoordinator_InvalidStageBoundsHidePresentation()
    {
        var windowModeService = new FakeWindowModeService { CurrentModeValue = ShellPlaybackWindowMode.Standard };
        var videoPresenter = new FakeVideoPresenter(new RectInt32(0, 0, 0, 0));
        var subtitlePresenter = new FakeSubtitlePresenter();
        var coordinator = new StageCoordinator(
            CreateUninitialized<Border>(),
            windowModeService,
            videoPresenter,
            subtitlePresenter,
            () => new FakeFullscreenOverlayWindow(),
            new FakeStageOverlayTimer());

        coordinator.PresentSubtitles(
            new BabelPlayer.WinUI.SubtitlePresentationModel
            {
                IsVisible = true,
                PrimaryText = "Hello"
            },
            new ShellSubtitleStyle(),
            hasLoadedMedia: true);

        Assert.Equal(0, subtitlePresenter.PresentCallCount);
        Assert.True(subtitlePresenter.HideCallCount > 0);
    }

    [Fact]
    public void ProviderAvailabilityService_ResolvesLocalLlamaAvailabilityFromConfiguredPath()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var llamaPath = Path.Combine(directory.FullName, "llama-server.exe");
            File.WriteAllText(llamaPath, string.Empty);
            var credentialStore = new FakeCredentialStore();
            credentialStore.SaveLlamaCppServerPath(llamaPath);
            var service = new ProviderAvailabilityService(
                new ProviderCompositionFactory(),
                new CredentialFacade(credentialStore),
                _ => null);

            Assert.Equal(llamaPath, service.ResolveLlamaCppServerPath());
            Assert.Equal("local:hymt-1.8b", service.ResolvePersistedTranslationModelKey("local:hymt-1.8b"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SubtitleWorkflowController_UsesSharedProviderCompositionForLocalWarmup()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var llamaPath = Path.Combine(directory.FullName, "llama-server.exe");
            File.WriteAllText(llamaPath, string.Empty);
            var credentialStore = new FakeCredentialStore();
            credentialStore.SaveLlamaCppServerPath(llamaPath);
            var credentialFacade = new CredentialFacade(credentialStore);
            var availabilityService = new ProviderAvailabilityService(
                new ProviderCompositionFactory(),
                credentialFacade,
                _ => null);
            var subtitleTranslator = new RecordingSubtitleTranslator();
            var runtimeProvisioner = new CountingRuntimeProvisioner();
            var controller = TestWorkflowControllerFactory.Create(
                credentialFacade,
                environmentVariableReader: _ => null,
                providerAvailabilityService: availabilityService,
                subtitleTranslator: subtitleTranslator,
                runtimeProvisioner: runtimeProvisioner);

            var applied = await controller.SelectTranslationModelAsync("local:hymt-1.8b");

            Assert.True(applied);
            Assert.Equal(1, subtitleTranslator.WarmupCallCount);
            Assert.Equal(0, runtimeProvisioner.EnsureLlamaRuntimeReadyCallCount);
            Assert.Equal("local:hymt-1.8b", controller.Snapshot.SelectedTranslationModelKey);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static StageCoordinator CreateStageCoordinator(
        out FakeFullscreenOverlayWindow overlayWindow,
        out FakeSubtitlePresenter subtitlePresenter,
        out FakeVideoPresenter videoPresenter)
    {
        var windowModeService = new FakeWindowModeService { CurrentModeValue = ShellPlaybackWindowMode.Fullscreen };
        videoPresenter = new FakeVideoPresenter(new RectInt32(10, 20, 640, 360));
        subtitlePresenter = new FakeSubtitlePresenter();
        var createdOverlayWindow = new FakeFullscreenOverlayWindow();
        var coordinator = new StageCoordinator(
            CreateUninitialized<Border>(),
            windowModeService,
            videoPresenter,
            subtitlePresenter,
            () => createdOverlayWindow,
            new FakeStageOverlayTimer());
        overlayWindow = createdOverlayWindow;
        return coordinator;
    }

    private static T CreateUninitialized<T>() where T : class
        => (T)FormatterServices.GetUninitializedObject(typeof(T));

    private sealed class FakeWindowModeService : IWindowModeService
    {
        public ShellPlaybackWindowMode CurrentModeValue { get; set; }

        public ShellPlaybackWindowMode CurrentMode => CurrentModeValue;

        public DisplayBounds GetCurrentDisplayBounds(bool workArea = false) => new(0, 0, 1920, 1080);

        public Task SetModeAsync(ShellPlaybackWindowMode mode, CancellationToken cancellationToken = default)
        {
            CurrentModeValue = mode;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVideoPresenter : IVideoPresenter
    {
        private readonly FrameworkElement _view = CreateUninitialized<Border>();
        private readonly RectInt32 _stageBounds;

        public FakeVideoPresenter(RectInt32 stageBounds)
        {
            _stageBounds = stageBounds;
        }

        public event Action? InputActivity;
        public event Action? FullscreenExitRequested;
        public event Func<ShortcutKeyInput, bool>? ShortcutKeyPressed;

        public FrameworkElement View => _view;

        public void Initialize(Window ownerWindow, IPlaybackHostRuntime playbackRuntime)
        {
        }

        public void RequestBoundsSync()
        {
        }

        public IDisposable SuppressPresentation() => new NullScope();

        public RectInt32 GetStageBounds(FrameworkElement relativeTo) => _stageBounds;
    }

    private sealed class FakeSubtitlePresenter : ISubtitlePresenter
    {
        public int HideCallCount { get; private set; }
        public int PresentCallCount { get; private set; }
        public int LastBottomOffset { get; private set; }

        public void Hide()
        {
            HideCallCount++;
        }

        public void ApplyStyle(ShellSubtitleStyle style)
        {
        }

        public void Present(BabelPlayer.WinUI.SubtitlePresentationModel model, RectInt32 stageBounds, int bottomOffset)
        {
            PresentCallCount++;
            LastBottomOffset = bottomOffset;
        }
    }

    private sealed class FakeFullscreenOverlayWindow : IFullscreenOverlayWindow
    {
        public event Action? ActivityDetected;
        public event Action<bool>? InteractionStateChanged;

        public Button PlayPauseButton { get; } = CreateUninitialized<Button>();
        public Button SubtitleToggleButton { get; } = CreateUninitialized<Button>();
        public DropDownButton SubtitleModeButton { get; } = CreateUninitialized<DropDownButton>();
        public DropDownButton SubtitleStyleButton { get; } = CreateUninitialized<DropDownButton>();
        public Button PipButton { get; } = CreateUninitialized<Button>();
        public Button ImmersiveButton { get; } = CreateUninitialized<Button>();
        public DropDownButton SettingsButton { get; } = CreateUninitialized<DropDownButton>();
        public Button ExitFullscreenButton { get; } = CreateUninitialized<Button>();
        public Slider PositionSlider { get; } = CreateUninitialized<Slider>();
        public TextBlock CurrentTimeTextBlock { get; } = CreateUninitialized<TextBlock>();
        public TextBlock DurationTextBlock { get; } = CreateUninitialized<TextBlock>();
        public bool IsOverlayVisible { get; private set; }

        public void ShowOverlay(RectInt32 displayBounds)
        {
            IsOverlayVisible = true;
        }

        public void HideOverlay()
        {
            IsOverlayVisible = false;
        }

        public void PositionOverlay(RectInt32 displayBounds)
        {
        }

        public void CloseOverlay()
        {
            IsOverlayVisible = false;
        }
    }

    private sealed class FakeStageOverlayTimer : IStageOverlayTimer
    {
        public event Action? Tick;

        public bool IsEnabled { get; private set; }

        public void Start()
        {
            IsEnabled = true;
        }

        public void Stop()
        {
            IsEnabled = false;
        }
    }

    private sealed class RecordingSubtitleTranslator : ISubtitleTranslator
    {
        public int WarmupCallCount { get; private set; }

        public event Action<LocalTranslationRuntimeStatus>? RuntimeStatusChanged;

        public Task WarmupAsync(TranslationModelSelection selection, CancellationToken cancellationToken)
        {
            WarmupCallCount++;
            return Task.CompletedTask;
        }

        public Task<string> TranslateAsync(TranslationModelSelection selection, string text, CancellationToken cancellationToken)
            => Task.FromResult(text);

        public Task<IReadOnlyList<string>> TranslateBatchAsync(TranslationModelSelection selection, IReadOnlyList<string> texts, CancellationToken cancellationToken)
            => Task.FromResult(texts);
    }

    private sealed class CountingRuntimeProvisioner : IRuntimeProvisioner
    {
        public int EnsureLlamaRuntimeReadyCallCount { get; private set; }

        public Task<string> EnsureLlamaCppAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
            => Task.FromResult("llama");

        public Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
            => Task.FromResult("ffmpeg");

        public Task<bool> EnsureLlamaCppRuntimeReadyAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        {
            EnsureLlamaRuntimeReadyCallCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

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
        public bool GetAutoTranslateEnabled() => false;
        public void SaveAutoTranslateEnabled(bool enabled) { }
        public string? GetLlamaCppServerPath() => Get("llama-server");
        public void SaveLlamaCppServerPath(string path) => Set("llama-server", path);
        public string? GetLlamaCppRuntimeVersion() => Get("llama-version");
        public void SaveLlamaCppRuntimeVersion(string version) => Set("llama-version", version);
        public string? GetLlamaCppRuntimeSource() => Get("llama-source");
        public void SaveLlamaCppRuntimeSource(string source) => Set("llama-source", source);

        private string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        private void Set(string key, string? value) => _values[key] = value;
    }

    private sealed class NullScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

#pragma warning restore CS0067
#pragma warning restore SYSLIB0050
