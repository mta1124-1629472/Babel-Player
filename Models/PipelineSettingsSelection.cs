namespace Babel.Player.Models;

public sealed record PipelineSettingsSelection(
    string TranscriptionProvider,
    string TranscriptionModel,
    string TranslationProvider,
    string TranslationModel,
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
