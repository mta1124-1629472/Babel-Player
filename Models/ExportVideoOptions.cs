namespace Babel.Player.Models;

public sealed record ExportVideoOptions(
    string OutputPath,
    bool IncludeTtsAudio = false,
    bool IncludeSoftCaptions = true,
    bool BurnInCaptions = false,
    bool OverwriteExisting = false,
    string? Encoder = null,
    /// <summary>When set, mux this file as the dubbed audio track instead of session TTS/mixed paths.</summary>
    string? DubAudioPathOverride = null);
