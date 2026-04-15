using System.Collections.Generic;

namespace Babel.Player.Models;

/// <summary>Pipeline output (dub/sub) language choices. Extend <see cref="All"/> when adding locales.</summary>
public sealed record PipelineTargetLanguageOption(string Code, string DisplayName)
{
    public static PipelineTargetLanguageOption English { get; } = new("en", "English");

    public static IReadOnlyList<PipelineTargetLanguageOption> All { get; } = [English];
}
