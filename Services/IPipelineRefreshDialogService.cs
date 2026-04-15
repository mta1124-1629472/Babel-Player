using System.Threading.Tasks;

namespace Babel.Player.Services;

public enum PipelineRefreshSection
{
    Transcription,
    Diarization,
    Translation,
}

/// <summary>
/// User choice for re-running an inference stage from the pipeline panel.
/// </summary>
public enum PipelineRefreshScope
{
    /// <summary>Re-run only this stage and stop.</summary>
    ThisStageOnly,

    /// <summary>Re-run this stage and every later stage (downstream only).</summary>
    RemainingPipeline,
}

/// <summary>
/// Modal prompts for pipeline section refresh scope (and dub-only confirm).
/// </summary>
public interface IPipelineRefreshDialogService
{
    /// <summary>Returns null if cancelled.</summary>
    Task<PipelineRefreshScope?> PromptRefreshScopeAsync(PipelineRefreshSection section);

    Task<bool> ConfirmRegenerateDubAsync();
}
