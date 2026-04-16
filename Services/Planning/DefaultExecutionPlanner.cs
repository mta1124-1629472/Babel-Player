using System;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;

namespace Babel.Player.Services.Planning;

internal sealed class DefaultExecutionPlanner : IExecutionPlanner
{
    public static DefaultExecutionPlanner Instance { get; } = new();

    private DefaultExecutionPlanner()
    {
    }

    public StageExecutionPlan CreatePlan(ExecutionPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Settings);

        var configured = CreateConfiguredPlan(request);
        if (!NeedsCloudCredentialFallback(configured.ProviderId, request.KeyStore))
            return configured with { Reason = "configured provider/runtime", IsFallback = false };

        return request.Stage switch
        {
            InferenceStage.Transcription => CreateFallbackPlan(
                request,
                fallbackProvider: ProviderNames.FasterWhisper,
                fallbackProfile: ComputeProfile.Cpu,
                reason: "missing cloud credential; falling back to local transcription"),
            InferenceStage.Translation => CreateFallbackPlan(
                request,
                fallbackProvider: ProviderNames.CTranslate2,
                fallbackProfile: ComputeProfile.Cpu,
                reason: "missing cloud credential; falling back to local translation"),
            InferenceStage.Tts => CreateFallbackPlan(
                request,
                fallbackProvider: ProviderNames.EdgeTts,
                fallbackProfile: ComputeProfile.Cloud,
                reason: "missing cloud credential; falling back to keyless cloud tts"),
            InferenceStage.Diarization => configured,
            _ => configured,
        };
    }

    private static StageExecutionPlan CreateConfiguredPlan(ExecutionPlanRequest request)
    {
        var settings = request.Settings;
        return request.Stage switch
        {
            InferenceStage.Transcription => BuildPlan(
                stage: request.Stage,
                providerId: InferenceRuntimeCatalog.NormalizeTranscriptionProvider(
                    settings.TranscriptionProfile,
                    settings.TranscriptionProvider),
                profile: settings.TranscriptionProfile,
                role: MapRole(request.Stage, settings.TranscriptionRuntime)),
            InferenceStage.Translation => BuildPlan(
                stage: request.Stage,
                providerId: InferenceRuntimeCatalog.NormalizeTranslationProvider(
                    settings.TranslationProfile,
                    settings.TranslationProvider),
                profile: settings.TranslationProfile,
                role: MapRole(request.Stage, settings.TranslationRuntime)),
            InferenceStage.Tts => BuildPlan(
                stage: request.Stage,
                providerId: InferenceRuntimeCatalog.NormalizeTtsProvider(
                    settings.TtsProfile,
                    settings.TtsProvider),
                profile: settings.TtsProfile,
                role: MapRole(request.Stage, settings.TtsRuntime)),
            InferenceStage.Diarization => BuildPlan(
                stage: request.Stage,
                providerId: InferenceRuntimeCatalog.NormalizeDiarizationProvider(settings.DiarizationProvider),
                profile: InferenceRuntimeCatalog.MapLegacyRuntimeToProfile(
                    InferenceRuntimeCatalog.InferDiarizationRuntime(settings.DiarizationProvider)),
                role: MapRole(request.Stage, InferenceRuntimeCatalog.InferDiarizationRuntime(settings.DiarizationProvider))),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Stage), request.Stage, "Unknown inference stage."),
        };
    }

    private static StageExecutionPlan CreateFallbackPlan(
        ExecutionPlanRequest request,
        string fallbackProvider,
        ComputeProfile fallbackProfile,
        string reason)
    {
        var fallbackRuntime = InferenceRuntimeCatalog.ResolveRuntime(fallbackProfile);
        return new StageExecutionPlan(
            request.Stage,
            fallbackProvider,
            fallbackRuntime,
            fallbackProfile,
            MapRole(request.Stage, fallbackRuntime),
            IsFallback: true,
            Reason: reason);
    }

    private static StageExecutionPlan BuildPlan(
        InferenceStage stage,
        string providerId,
        ComputeProfile profile,
        RuntimeRole role) =>
        new(
            stage,
            providerId,
            InferenceRuntimeCatalog.ResolveRuntime(profile),
            profile,
            role,
            IsFallback: false,
            Reason: "configured provider/runtime");

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

    private static bool NeedsCloudCredentialFallback(string providerId, ApiKeyStore? keyStore)
    {
        var credentialKey = providerId switch
        {
            ProviderNames.OpenAiWhisperApi => CredentialKeys.OpenAi,
            ProviderNames.GoogleStt => CredentialKeys.GoogleAi,
            ProviderNames.GeminiTranscription => CredentialKeys.GoogleGemini,
            ProviderNames.Deepl => CredentialKeys.Deepl,
            ProviderNames.OpenAi => CredentialKeys.OpenAi,
            ProviderNames.GeminiTranslation => CredentialKeys.GoogleGemini,
            ProviderNames.ElevenLabs => CredentialKeys.ElevenLabs,
            ProviderNames.OpenAiTts => CredentialKeys.OpenAi,
            ProviderNames.GoogleCloudTts => CredentialKeys.GoogleAi,
            _ => null,
        };

        if (credentialKey is null)
            return false;

        var key = keyStore?.GetKey(credentialKey);
        return string.IsNullOrWhiteSpace(key);
    }
}
