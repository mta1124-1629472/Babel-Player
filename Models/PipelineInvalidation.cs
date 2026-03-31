namespace Babel.Player.Models;

/// <summary>
/// Indicates which pipeline stages need to be re-run because provider settings have changed
/// since the current session was last processed. Returned by
/// <see cref="Services.SessionWorkflowCoordinator.CheckSettingsInvalidation"/>.
/// </summary>
public enum PipelineInvalidation
{
    /// <summary>All current results are still valid — nothing needs to re-run.</summary>
    None,

    /// <summary>TTS settings changed — only TTS needs to be regenerated.</summary>
    Tts,

    /// <summary>Translation settings or target language changed — translation and TTS must re-run.</summary>
    Translation,

    /// <summary>Transcription settings changed — everything must re-run from scratch.</summary>
    Transcription,
}
