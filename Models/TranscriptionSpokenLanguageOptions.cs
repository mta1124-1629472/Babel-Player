using System.Collections.Generic;

namespace Babel.Player.Models;

/// <summary>Optional ASR language hints (Whisper <c>language=</c>). <see cref="Code"/> null means auto-detect.</summary>
public sealed record SpokenLanguageOption(string? Code, string DisplayName)
{
    public static IReadOnlyList<SpokenLanguageOption> All { get; } =
    [
        new(null, "Auto-detect"),
        new("es", "Spanish"),
        new("en", "English"),
        new("fr", "French"),
        new("de", "German"),
        new("it", "Italian"),
        new("pt", "Portuguese"),
        new("ja", "Japanese"),
        new("zh", "Chinese"),
    ];
}
