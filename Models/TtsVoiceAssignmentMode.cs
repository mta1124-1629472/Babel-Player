namespace Babel.Player.Models;

/// <summary>How Piper/Edge TTS voices are chosen: one global default vs per-speaker in the Speaker Reference Wizard.</summary>
public enum TtsVoiceAssignmentMode
{
    /// <summary><see cref="Services.Settings.AppSettings.TtsVoice"/> applies to all speakers unless overridden in session.</summary>
    GlobalDefault = 0,

    /// <summary>Per-speaker voices in the wizard; <see cref="Services.Settings.AppSettings.TtsVoice"/> is fallback only.</summary>
    PerSpeaker = 1,
}
