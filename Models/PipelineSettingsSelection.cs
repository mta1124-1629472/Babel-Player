namespace Babel.Player.Models;

public sealed record PipelineSettingsSelection(
    InferenceRuntime TranscriptionRuntime,
    string TranscriptionProvider,
    string TranscriptionModel,
    InferenceRuntime TranslationRuntime,
    string TranslationProvider,
    string TranslationModel,
    InferenceRuntime TtsRuntime,
    string TtsProvider,
    string TtsVoice,
    string? TargetLanguage = null);

public sealed record PipelineSettingsApplyResult(
    PipelineInvalidation Invalidation,
    SessionWorkflowStage StageAfterApply,
    bool SettingsChanged,
    string StatusMessage);

public sealed record MediaReloadRequest(
    string IngestedMediaPath,
    bool AutoPlay,
    string Reason);
