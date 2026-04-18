using System;
using System.IO;
using System.Linq;
using Babel.Player.Models;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Services;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;

namespace BabelPlayer.Tests;

public sealed class GpuRuntimeSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-gpu-runtime-tests-{Guid.NewGuid():N}");
    private readonly AppLog _log;
    private readonly SessionSnapshotStore _store;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;

    public GpuRuntimeSelectionTests()
    {
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _store = new SessionSnapshotStore(Path.Combine(_dir, "session.json"), _log);
        _perSessionStore = new PerSessionSnapshotStore(Path.Combine(_dir, "sessions"), _log);
        _recentStore = new RecentSessionsStore(Path.Combine(_dir, "recent-sessions.json"), _log);
    }

    [Fact]
    public void EmbeddedPlaybackViewModel_NoCuda_HidesGpuRuntimeOption()
    {
        using var coordinator = CreateCoordinatorWithFakeRegistries(new AppSettings());
        coordinator.Initialize();
        coordinator.HardwareSnapshot = CreateHardwareSnapshot(isDetecting: false, hasCuda: false);

        using var viewModel = new EmbeddedPlaybackViewModel(coordinator);

        Assert.DoesNotContain(ComputeProfile.Gpu, viewModel.InferenceRuntimeOptions);
        Assert.Equal(
            [ComputeProfile.Cpu, ComputeProfile.Cloud],
            viewModel.InferenceRuntimeOptions);
    }

    [Fact]
    public void EmbeddedPlaybackViewModel_Detecting_KeepsGpuRuntimeOptionVisible()
    {
        using var coordinator = CreateCoordinatorWithFakeRegistries(new AppSettings());
        coordinator.Initialize();
        coordinator.HardwareSnapshot = HardwareSnapshot.Detecting;

        using var viewModel = new EmbeddedPlaybackViewModel(coordinator);

        Assert.Contains(ComputeProfile.Gpu, viewModel.InferenceRuntimeOptions);
    }

    [Fact]
    public void HardwareSnapshot_NoCuda_NormalizesPersistedGpuSelectionsUsingCpuCatalogs()
    {
        var settings = new AppSettings
        {
            TranscriptionProfile = ComputeProfile.Gpu,
            TranscriptionProvider = ProviderNames.Parakeet,
            TranscriptionModel = "parakeet-tdt-0.6b-v3",
            TranslationProfile = ComputeProfile.Gpu,
            TranslationProvider = ProviderNames.Nllb200,
            TranslationModel = "nllb-200-1.3B",
            TtsProfile = ComputeProfile.Gpu,
            TtsProvider = ProviderNames.Qwen,
            TtsVoice = QwenTtsCatalog.ModelIds[0],
            TargetLanguage = "fr",
            TranscriptionLanguageHint = "es",
        };

        using var coordinator = CreateCoordinatorWithRealRegistries(settings);
        coordinator.Initialize();

        coordinator.HardwareSnapshot = CreateHardwareSnapshot(isDetecting: false, hasCuda: false);

        Assert.Equal(ComputeProfile.Cpu, coordinator.CurrentSettings.TranscriptionProfile);
        Assert.Equal(ProviderNames.FasterWhisper, coordinator.CurrentSettings.TranscriptionProvider);
        Assert.Contains(
            coordinator.CurrentSettings.TranscriptionModel,
            coordinator.TranscriptionRegistry.GetAvailableModels(
                coordinator.CurrentSettings.TranscriptionProvider,
                ComputeProfile.Cpu,
                coordinator.CurrentSettings));

        Assert.Equal(ComputeProfile.Cpu, coordinator.CurrentSettings.TranslationProfile);
        Assert.Equal(ProviderNames.CTranslate2, coordinator.CurrentSettings.TranslationProvider);
        Assert.Contains(
            coordinator.CurrentSettings.TranslationModel,
            coordinator.TranslationRegistry.GetAvailableModels(
                coordinator.CurrentSettings.TranslationProvider,
                ComputeProfile.Cpu,
                coordinator.CurrentSettings));

        Assert.Equal(ComputeProfile.Cpu, coordinator.CurrentSettings.TtsProfile);
        Assert.Equal(ProviderNames.Piper, coordinator.CurrentSettings.TtsProvider);
        Assert.Contains(
            coordinator.CurrentSettings.TtsVoice,
            coordinator.TtsRegistry.GetAvailableModels(
                coordinator.CurrentSettings.TtsProvider,
                ComputeProfile.Cpu,
                coordinator.CurrentSettings));

        Assert.Equal("fr", coordinator.CurrentSettings.TargetLanguage);
        Assert.Equal("es", coordinator.CurrentSettings.TranscriptionLanguageHint);
    }

    [Fact]
    public void HardwareSnapshot_NoCuda_PreservesCompletedSessionWhileNormalizingPersistedGpuSelections()
    {
        var settings = new AppSettings
        {
            TranscriptionProfile = ComputeProfile.Gpu,
            TranscriptionProvider = ProviderNames.Parakeet,
            TranscriptionModel = "parakeet-tdt-0.6b-v3",
            TranslationProfile = ComputeProfile.Gpu,
            TranslationProvider = ProviderNames.Nllb200,
            TranslationModel = "nllb-200-1.3B",
            TtsProfile = ComputeProfile.Gpu,
            TtsProvider = ProviderNames.Qwen,
            TtsVoice = QwenTtsCatalog.ModelIds[0],
            TargetLanguage = "fr",
            TranscriptionLanguageHint = "es",
        };

        using var coordinator = CreateCoordinatorWithRealRegistries(settings);
        coordinator.Initialize();

        var now = DateTimeOffset.Parse("2025-01-15T12:00:00Z");
        var sourceMediaPath = WriteFile("source.mp4", "media");
        var ingestedMediaPath = WriteFile("ingested.mp4", "media");
        var ttsPath = WriteFile("dub.mp3", "audio");

        coordinator.CurrentSession = WorkflowSessionSnapshot.CreateNew(now) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            SourceMediaPath = sourceMediaPath,
            IngestedMediaPath = ingestedMediaPath,
            MediaLoadedAtUtc = now,
            SourceLanguage = "es",
            TargetLanguage = "fr",
            TtsPath = ttsPath,
            TtsGeneratedAtUtc = now,
            StatusMessage = "Completed with GPU settings.",
            TranscriptionRuntime = InferenceRuntime.Containerized,
            TranscriptionProvider = ProviderNames.Parakeet,
            TranscriptionModel = "parakeet-tdt-0.6b-v3",
            TranscriptionLanguageHint = "es",
            TranslationRuntime = InferenceRuntime.Containerized,
            TranslationProvider = ProviderNames.Nllb200,
            TranslationModel = "nllb-200-1.3B",
            TtsRuntime = InferenceRuntime.Containerized,
            TtsProvider = ProviderNames.Qwen,
            TtsVoice = QwenTtsCatalog.ModelIds[0],
        };
        coordinator.SaveCurrentSession();

        coordinator.HardwareSnapshot = CreateHardwareSnapshot(isDetecting: false, hasCuda: false);

        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        Assert.Equal(ttsPath, coordinator.CurrentSession.TtsPath);
        Assert.Equal("Completed with GPU settings.", coordinator.CurrentSession.StatusMessage);

        var persisted = _store.Load().Snapshot;
        Assert.NotNull(persisted);
        Assert.Equal(SessionWorkflowStage.TtsGenerated, persisted!.Stage);
        Assert.Equal(ttsPath, persisted.TtsPath);
        Assert.Equal("Completed with GPU settings.", persisted.StatusMessage);

        Assert.Equal(ComputeProfile.Cpu, coordinator.CurrentSettings.TranscriptionProfile);
        Assert.Equal(ComputeProfile.Cpu, coordinator.CurrentSettings.TranslationProfile);
        Assert.Equal(ComputeProfile.Cpu, coordinator.CurrentSettings.TtsProfile);
    }

    public void Dispose()
    {
        _log.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private SessionWorkflowCoordinator CreateCoordinatorWithFakeRegistries(AppSettings settings)
    {
        var registries = new RegistryBundle(
            _perSessionStore,
            _recentStore,
            new FakeTranscriptionRegistry(),
            new FakeTranslationRegistry(),
            new FakeTtsRegistry());
        var coreServices = new CoordinatorCoreServices(_store, _log, settings);
        return new SessionWorkflowCoordinator(coreServices, registries);
    }

    private SessionWorkflowCoordinator CreateCoordinatorWithRealRegistries(AppSettings settings)
    {
        var registries = new RegistryBundle(
            _perSessionStore,
            _recentStore,
            new TranscriptionRegistry(_log),
            new TranslationRegistry(_log),
            new TtsRegistry(_log));
        var coreServices = new CoordinatorCoreServices(_store, _log, settings);
        return new SessionWorkflowCoordinator(coreServices, registries);
    }

    private static HardwareSnapshot CreateHardwareSnapshot(bool isDetecting, bool hasCuda) =>
        new(
            IsDetecting: isDetecting,
            CpuName: "Test CPU",
            CpuCores: 8,
            HasAvx: true,
            HasAvx2: true,
            HasAvx512F: false,
            SystemRamGb: 16,
            GpuName: hasCuda ? "NVIDIA GeForce RTX 3060" : null,
            GpuVramMb: hasCuda ? 12288 : null,
            HasCuda: hasCuda,
            CudaVersion: hasCuda ? "12.8" : null,
            HasOpenVino: false,
            OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: hasCuda,
            IsVsrDriverSufficient: hasCuda,
            NvidiaDriverVersion: hasCuda ? "551.23" : null,
            GpuComputeCapability: hasCuda ? "8.6" : null);

    private string WriteFile(string fileName, string contents)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, contents);
        return path;
    }
}
