using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class VsrDiagnosticsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _storePath;
    private readonly string _perSessionDir;
    private readonly string _recentPath;
    private readonly string _settingsPath;
    private readonly AppLog _log;

    public VsrDiagnosticsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-vsr-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _storePath = Path.Combine(_dir, "session.json");
        _perSessionDir = Path.Combine(_dir, "sessions");
        _recentPath = Path.Combine(_dir, "recent-sessions.json");
        _settingsPath = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_perSessionDir);
        _log = new AppLog(Path.Combine(_dir, "vsr.log"));
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public void CreateVsrDiagnosticSnapshot_MapsSkippedReasonForNoUpscaling()
    {
        var plan = LibMpvEmbeddedTransport.EvaluateVsrFilterPlan(
            videoWidth: 1280,
            videoHeight: 720,
            displayWidth: 1280,
            displayHeight: 720,
            monitorWidth: 1280,
            monitorHeight: 720,
            hwPixelFormat: "nv12");

        var snapshot = LibMpvEmbeddedTransport.CreateVsrDiagnosticSnapshot(
            "display-size-updated",
            new VideoPlaybackOptions(UseGpuNext: true, VsrEnabled: true),
            plan,
            backendResultCode: null,
            videoOutput: "gpu-next",
            gpuContext: "d3d11",
            hwdecCurrent: "d3d11va");

        Assert.Equal(VsrDiagnosticState.Skipped, snapshot.State);
        Assert.Equal("skip", snapshot.ResolvedPlan);
        Assert.Equal("no-upscaling-required", snapshot.ReasonCode);
        Assert.Equal("no upscaling is required", snapshot.ReasonText);
        Assert.Equal("VSR skipped: no upscaling is required", snapshot.PlaybackStatusText);
        Assert.Equal(1280, snapshot.MonitorWidth);
        Assert.Equal(720, snapshot.MonitorHeight);
    }

    [Fact]
    public void CreateVsrDiagnosticSnapshot_MapsRejectedMpvCommandResult()
    {
        var plan = LibMpvEmbeddedTransport.EvaluateVsrFilterPlan(
            videoWidth: 1280,
            videoHeight: 720,
            displayWidth: 1538,
            displayHeight: 789,
            monitorWidth: 1538,
            monitorHeight: 789,
            hwPixelFormat: "nv12");

        var snapshot = LibMpvEmbeddedTransport.CreateVsrDiagnosticSnapshot(
            "display-size-updated",
            new VideoPlaybackOptions(UseGpuNext: true, VsrEnabled: true),
            plan,
            backendResultCode: -12,
            videoOutput: "gpu-next",
            gpuContext: "d3d11",
            hwdecCurrent: "d3d11va");

        Assert.Equal(VsrDiagnosticState.Rejected, snapshot.State);
        Assert.Equal("apply", snapshot.ResolvedPlan);
        Assert.Equal("command-rejected", snapshot.ReasonCode);
        Assert.Equal("libmpv rejected the vf add command", snapshot.BackendResultLabel);
        Assert.Equal("libmpv rejected the vf add command", snapshot.ReasonText);
        Assert.Contains("rejected", snapshot.PlaybackStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1538, snapshot.MonitorWidth);
        Assert.Equal(789, snapshot.MonitorHeight);
    }

    [Fact]
    public void CoordinatorAndViewModels_ProjectLatestVsrDiagnostic()
    {
        var settings = CreateSettings();
        var coordinator = CreateCoordinator(settings);
        coordinator.Initialize();
        coordinator.HardwareSnapshot = CreateHardwareSnapshot(
            isRtxCapable: true,
            isVsrDriverSufficient: true,
            nvidiaDriverVersion: "572.16");

        var playback = new EmbeddedPlaybackViewModel(coordinator);
        using var settingsVm = new SettingsViewModel(
            new SettingsService(_settingsPath, _log),
            coordinator,
            (Window)RuntimeHelpers.GetUninitializedObject(typeof(Window)),
            new ModelsTabViewModel(new ModelDownloader(_log), coordinator));

        var rejectedSnapshot = LibMpvEmbeddedTransport.CreateVsrDiagnosticSnapshot(
            "display-size-updated",
            new VideoPlaybackOptions(UseGpuNext: true, VsrEnabled: true),
            LibMpvEmbeddedTransport.EvaluateVsrFilterPlan(
                videoWidth: 1280,
                videoHeight: 720,
                displayWidth: 1538,
                displayHeight: 789,
                monitorWidth: 1538,
                monitorHeight: 789,
                hwPixelFormat: "nv12"),
            backendResultCode: -12,
            videoOutput: "gpu-next",
            gpuContext: "d3d11",
            hwdecCurrent: "d3d11va");

        coordinator.RecordVsrDiagnosticSnapshot(rejectedSnapshot);

        Assert.True(playback.HasVsrPlaybackStatus);
        Assert.Equal("VSR rejected: libmpv rejected the vf add command", playback.VsrPlaybackStatusText);

        Assert.Contains("RTX-class GPU detected", settingsVm.VsrSupportHintText, StringComparison.Ordinal);
        Assert.Contains("gpu-next is enabled", settingsVm.VsrRequestedStateText, StringComparison.Ordinal);
        Assert.Contains("libmpv rejected the filter command", settingsVm.VsrResolvedStateText, StringComparison.Ordinal);
        Assert.Contains("result -12", settingsVm.VsrReasonText, StringComparison.Ordinal);
        Assert.Contains("d3d11vpp=scaling-mode=nvidia", settingsVm.VsrFilterText, StringComparison.Ordinal);
    }

    private SessionWorkflowCoordinator CreateCoordinator(AppSettings settings)
    {
        var store = new SessionSnapshotStore(_storePath, _log);
        var perSessionStore = new PerSessionSnapshotStore(_perSessionDir, _log);
        var recentStore = new RecentSessionsStore(_recentPath, _log);
        return new SessionWorkflowCoordinator(
            store,
            _log,
            settings,
            perSessionStore,
            recentStore,
            new FakeTranscriptionRegistry(new FakeTranscriptionProvider()),
            new FakeTranslationRegistry(new FakeTranslationProvider()),
            new FakeTtsRegistry(new FakeTtsProvider()));
    }

    private static AppSettings CreateSettings() =>
        new()
        {
            TranscriptionProfile = ComputeProfile.Cpu,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "fake-whisper",
            TranslationProfile = ComputeProfile.Cpu,
            TranslationProvider = ProviderNames.CTranslate2,
            TranslationModel = "fake-translation-model",
            TtsProfile = ComputeProfile.Cloud,
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "fake-voice",
            TargetLanguage = "en",
            VideoUseGpuNext = true,
            VideoVsrEnabled = true,
        };

    private static HardwareSnapshot CreateHardwareSnapshot(
        bool isRtxCapable,
        bool isVsrDriverSufficient,
        string? nvidiaDriverVersion) =>
        new(
            IsDetecting: false,
            CpuName: "Fake CPU",
            CpuCores: 12,
            HasAvx: true,
            HasAvx2: true,
            HasAvx512F: false,
            SystemRamGb: 32,
            GpuName: "NVIDIA GeForce RTX 5070",
            GpuVramMb: 12288,
            HasCuda: true,
            CudaVersion: "12.8",
            HasOpenVino: false,
            OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: isRtxCapable,
            IsVsrDriverSufficient: isVsrDriverSufficient,
            NvidiaDriverVersion: nvidiaDriverVersion,
            IsHdrDisplayAvailable: false,
            GpuComputeCapability: "12.0");

    private sealed class FakeTranscriptionRegistry : ITranscriptionRegistry
    {
        private readonly FakeTranscriptionProvider _provider;

        public FakeTranscriptionRegistry(FakeTranscriptionProvider provider) => _provider = provider;

        public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null) =>
        [
            new ProviderDescriptor(
                ProviderNames.FasterWhisper,
                "Fake transcription",
                false,
                null,
                ["fake-whisper"],
                SupportedRuntimes: [InferenceRuntime.Local],
                DefaultRuntime: InferenceRuntime.Local)
        ];

        public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
            ["fake-whisper"];

        public ITranscriptionProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null) =>
            _provider;

        public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null) =>
            ProviderReadiness.Ready;

        public Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null) =>
            Task.FromResult(true);
    }

    private sealed class FakeTranslationRegistry : ITranslationRegistry
    {
        private readonly FakeTranslationProvider _provider;

        public FakeTranslationRegistry(FakeTranslationProvider provider) => _provider = provider;

        public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null) =>
        [
            new ProviderDescriptor(
                ProviderNames.CTranslate2,
                "Fake translation",
                false,
                null,
                ["fake-translation-model"],
                SupportedRuntimes: [InferenceRuntime.Local],
                DefaultRuntime: InferenceRuntime.Local)
        ];

        public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
            ["fake-translation-model"];

        public ITranslationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null) =>
            _provider;

        public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null) =>
            ProviderReadiness.Ready;

        public Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null) =>
            Task.FromResult(true);
    }

    private sealed class FakeTtsRegistry : ITtsRegistry
    {
        private readonly FakeTtsProvider _provider;

        public FakeTtsRegistry(FakeTtsProvider provider) => _provider = provider;

        public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null) =>
        [
            new ProviderDescriptor(
                ProviderNames.EdgeTts,
                "Fake TTS",
                false,
                null,
                ["fake-voice"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud)
        ];

        public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
            ["fake-voice"];

        public ITtsProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null) =>
            _provider;

        public ProviderReadiness CheckReadiness(string providerId, string modelOrVoice, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null) =>
            ProviderReadiness.Ready;

        public Task<bool> EnsureModelAsync(string providerId, string modelOrVoice, AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null) =>
            Task.FromResult(true);
    }

    private sealed class FakeTranscriptionProvider : ITranscriptionProvider
    {
        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) => ProviderReadiness.Ready;

        public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranscriptionResult(true, [], "en", 1.0, null));
    }

    private sealed class FakeTranslationProvider : ITranslationProvider
    {
        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) => ProviderReadiness.Ready;

        public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationResult(true, [], request.SourceLanguage, request.TargetLanguage, null));

        public Task<TranslationResult> TranslateSingleSegmentAsync(SingleSegmentTranslationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationResult(true, [], request.SourceLanguage, request.TargetLanguage, null));
    }

    private sealed class FakeTtsProvider : ITtsProvider
    {
        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) => ProviderReadiness.Ready;

        public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<TtsResult> GenerateTtsAsync(TtsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TtsResult(true, Path.Combine(Path.GetTempPath(), "fake.mp3"), request.VoiceName, 0, null));

        public Task<TtsResult> GenerateSegmentTtsAsync(SingleSegmentTtsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TtsResult(true, Path.Combine(Path.GetTempPath(), "fake-segment.mp3"), request.VoiceName, 0, null));
    }
}
