using System.Collections.Generic;

namespace Babel.Player.Models;

public sealed record ExportVideoPlan(
    string SourceMediaPath,
    string OutputPath,
    bool IncludeTtsAudio,
    bool IncludeSoftCaptions,
    bool BurnInCaptions,
    IReadOnlyList<string> InputFiles,
    IReadOnlyList<string> FfmpegArguments,
    string? SubtitleFilePath);
