using System;
using Babel.Player.Models;
using Babel.Player.Services.Planning;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    internal StageExecutionPlan ResolveAndApplyExecutionPlan(InferenceStage stage)
    {
        var fallbackPlan = CreateConfiguredStagePlan(stage, reason: "planner fallback to configured settings", isFallback: true);
        StageExecutionPlan effectivePlan;

        try
        {
            var request = new ExecutionPlanRequest(stage, CurrentSettings, KeyStore, HardwareSnapshot);
            var planned = _executionPlanner.CreatePlan(request);
            effectivePlan = IsValidPlan(planned, stage) ? planned : fallbackPlan;
        }
        catch (Exception ex)
        {
            _log.Warning($"Execution planner failed for stage '{stage}': {ex.Message}");
            effectivePlan = fallbackPlan;
        }

        ApplyStagePlan(effectivePlan);
        LogPlanDecision(effectivePlan, fallbackPlan);
        return effectivePlan;
    }

    private static bool IsValidPlan(StageExecutionPlan? plan, InferenceStage stage) =>
        plan is not null
        && plan.Stage == stage
        && !string.IsNullOrWhiteSpace(plan.ProviderId);

    private StageExecutionPlan CreateConfiguredStagePlan(InferenceStage stage, string reason, bool isFallback)
    {
        var settings = CurrentSettings;
        return stage switch
        {
            InferenceStage.Transcription => new StageExecutionPlan(
                stage,
                settings.TranscriptionProvider,
                settings.TranscriptionRuntime,
                settings.TranscriptionProfile,
                MapRole(stage, settings.TranscriptionRuntime),
                isFallback,
                reason),
            InferenceStage.Translation => new StageExecutionPlan(
                stage,
                settings.TranslationProvider,
                settings.TranslationRuntime,
                settings.TranslationProfile,
                MapRole(stage, settings.TranslationRuntime),
                isFallback,
                reason),
            InferenceStage.Tts => new StageExecutionPlan(
                stage,
                settings.TtsProvider,
                settings.TtsRuntime,
                settings.TtsProfile,
                MapRole(stage, settings.TtsRuntime),
                isFallback,
                reason),
            InferenceStage.Diarization => new StageExecutionPlan(
                stage,
                settings.DiarizationProvider,
                InferenceRuntimeCatalog.InferDiarizationRuntime(settings.DiarizationProvider),
                InferenceRuntimeCatalog.MapLegacyRuntimeToProfile(
                    InferenceRuntimeCatalog.InferDiarizationRuntime(settings.DiarizationProvider)),
                MapRole(stage, InferenceRuntimeCatalog.InferDiarizationRuntime(settings.DiarizationProvider)),
                isFallback,
                reason),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown inference stage."),
        };
    }

    private void ApplyStagePlan(StageExecutionPlan plan)
    {
        switch (plan.Stage)
        {
            case InferenceStage.Transcription:
                if (!string.Equals(CurrentSettings.TranscriptionProvider, plan.ProviderId, StringComparison.Ordinal)
                    || CurrentSettings.TranscriptionProfile != plan.Profile)
                {
                    _transcriptionService = null;
                }
                CurrentSettings.TranscriptionProvider = plan.ProviderId;
                CurrentSettings.TranscriptionProfile = plan.Profile;
                break;

            case InferenceStage.Translation:
                if (!string.Equals(CurrentSettings.TranslationProvider, plan.ProviderId, StringComparison.Ordinal)
                    || CurrentSettings.TranslationProfile != plan.Profile)
                {
                    _translationService = null;
                }
                CurrentSettings.TranslationProvider = plan.ProviderId;
                CurrentSettings.TranslationProfile = plan.Profile;
                break;

            case InferenceStage.Tts:
                if (!string.Equals(CurrentSettings.TtsProvider, plan.ProviderId, StringComparison.Ordinal)
                    || CurrentSettings.TtsProfile != plan.Profile)
                {
                    _ttsService = null;
                }
                CurrentSettings.TtsProvider = plan.ProviderId;
                CurrentSettings.TtsProfile = plan.Profile;
                break;

            case InferenceStage.Diarization:
                CurrentSettings.DiarizationProvider = plan.ProviderId;
                break;
        }
    }

    private void LogPlanDecision(StageExecutionPlan plan, StageExecutionPlan fallbackPlan)
    {
        var fallbackReason = plan.IsFallback ? $" fallback_reason=\"{plan.Reason}\"" : string.Empty;
        _log.Debug(
            $"Execution plan decision stage={plan.Stage} " +
            $"requested_provider={fallbackPlan.ProviderId} requested_runtime={fallbackPlan.Runtime} " +
            $"selected_provider={plan.ProviderId} selected_runtime={plan.Runtime} selected_role={plan.Role} " +
            $"selected_profile={plan.Profile} used_fallback={(plan.IsFallback ? "true" : "false")}{fallbackReason}");
    }

    private static RuntimeRole MapRole(InferenceStage stage, InferenceRuntime runtime)
    {
        return runtime switch
        {
            InferenceRuntime.Containerized => RuntimeRole.Containerized,
            InferenceRuntime.Cloud => RuntimeRole.Cloud,
            _ when stage == InferenceStage.Tts => RuntimeRole.CpuVoice,
            _ when stage == InferenceStage.Diarization => RuntimeRole.CpuDiar,
            _ => RuntimeRole.CpuNlp,
        };
    }

}
