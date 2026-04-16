using System;
using System.IO;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Planning;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class ExecutionPlannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-planner-tests-{Guid.NewGuid():N}");

    public ExecutionPlannerTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void DefaultPlanner_UsesConfiguredProvider_WhenCredentialsSatisfied()
    {
        var settings = new AppSettings
        {
            TranscriptionProfile = ComputeProfile.Cpu,
            TranscriptionProvider = ProviderNames.FasterWhisper,
        };
        var request = new ExecutionPlanRequest(
            InferenceStage.Transcription,
            settings,
            KeyStore: null,
            HardwareSnapshot.Detecting);

        var plan = DefaultExecutionPlanner.Instance.CreatePlan(request);

        Assert.Equal(InferenceStage.Transcription, plan.Stage);
        Assert.Equal(ProviderNames.FasterWhisper, plan.ProviderId);
        Assert.Equal(InferenceRuntime.Local, plan.Runtime);
        Assert.Equal(RuntimeRole.CpuNlp, plan.Role);
        Assert.False(plan.IsFallback);
    }

    [Fact]
    public void DefaultPlanner_FallsBackFromCloudTranslation_WhenKeyMissing()
    {
        var settings = new AppSettings
        {
            TranslationProfile = ComputeProfile.Cloud,
            TranslationProvider = ProviderNames.OpenAi,
        };
        var keyStore = new ApiKeyStore(new FileSystemCredentialProvider(Path.Combine(_dir, "api-keys.json")));
        var request = new ExecutionPlanRequest(
            InferenceStage.Translation,
            settings,
            keyStore,
            HardwareSnapshot.Detecting);

        var plan = DefaultExecutionPlanner.Instance.CreatePlan(request);

        Assert.Equal(ProviderNames.CTranslate2, plan.ProviderId);
        Assert.Equal(ComputeProfile.Cpu, plan.Profile);
        Assert.Equal(InferenceRuntime.Local, plan.Runtime);
        Assert.Equal(RuntimeRole.CpuNlp, plan.Role);
        Assert.True(plan.IsFallback);
    }

    [Fact]
    public void Coordinator_AppliesPlannerDecision_ForStageRouting()
    {
        var log = new AppLog(Path.Combine(_dir, "coordinator.log"));
        var settings = new AppSettings
        {
            TranslationProfile = ComputeProfile.Cloud,
            TranslationProvider = ProviderNames.OpenAi,
        };

        var coordinator = CreateCoordinator(settings, log, DefaultExecutionPlanner.Instance);
        var applied = coordinator.ResolveAndApplyExecutionPlan(InferenceStage.Translation);

        Assert.Equal(ProviderNames.CTranslate2, applied.ProviderId);
        Assert.Equal(ProviderNames.CTranslate2, coordinator.CurrentSettings.TranslationProvider);
        Assert.Equal(ComputeProfile.Cpu, coordinator.CurrentSettings.TranslationProfile);
    }

    [Fact]
    public void Coordinator_FallsBackToConfiguredSettings_WhenPlannerReturnsInvalidPlan()
    {
        var log = new AppLog(Path.Combine(_dir, "invalid-plan.log"));
        var settings = new AppSettings
        {
            TranscriptionProfile = ComputeProfile.Cpu,
            TranscriptionProvider = ProviderNames.FasterWhisper,
        };

        var coordinator = CreateCoordinator(settings, log, new InvalidExecutionPlanner());
        var applied = coordinator.ResolveAndApplyExecutionPlan(InferenceStage.Transcription);

        Assert.Equal(ProviderNames.FasterWhisper, applied.ProviderId);
        Assert.Equal(ProviderNames.FasterWhisper, coordinator.CurrentSettings.TranscriptionProvider);
        Assert.True(applied.IsFallback);
    }

    private SessionWorkflowCoordinator CreateCoordinator(
        AppSettings settings,
        AppLog log,
        IExecutionPlanner planner)
    {
        var store = new SessionSnapshotStore(Path.Combine(_dir, $"{Guid.NewGuid():N}-state.json"), log);
        var perSessionStore = new PerSessionSnapshotStore(Path.Combine(_dir, $"{Guid.NewGuid():N}-sessions"), log);
        var recentStore = new RecentSessionsStore(Path.Combine(_dir, $"{Guid.NewGuid():N}-recent.json"), log);

        var registries = new RegistryBundle(
            perSessionStore,
            recentStore,
            new TranscriptionRegistry(log),
            new TranslationRegistry(log),
            new TtsRegistry(log));

        var options = new CoordinatorOptions
        {
            ExecutionPlanner = planner,
        };
        var core = new CoordinatorCoreServices(store, log, settings);
        return new SessionWorkflowCoordinator(core, registries, options);
    }

    private sealed class InvalidExecutionPlanner : IExecutionPlanner
    {
        public StageExecutionPlan CreatePlan(ExecutionPlanRequest request) =>
            new(
                request.Stage,
                ProviderId: string.Empty,
                Runtime: InferenceRuntime.Local,
                Profile: ComputeProfile.Cpu,
                Role: RuntimeRole.CpuNlp,
                IsFallback: false,
                Reason: "invalid test output");
    }
}
