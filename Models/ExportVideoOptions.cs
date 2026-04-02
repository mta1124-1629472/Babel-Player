namespace Babel.Player.Models;

public sealed record ExportVideoOptions(
    string OutputPath,
    bool IncludeTtsAudio = false,
    bool IncludeSoftCaptions = true,
    bool BurnInCaptions = false,
    bool OverwriteExisting = false,
    string? Encoder = null);
