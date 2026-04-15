using System;
using System.Collections.Generic;
using System.Linq;
using Babel.Player.Models.LanguageSupport;

namespace Babel.Player.Models;

/// <summary>Pipeline output (dub/sub) language choices. Kept in sync with <see cref="NllbLanguageCatalog"/> for local NMT.</summary>
public sealed record PipelineTargetLanguageOption(string Code, string DisplayName)
{
    public static PipelineTargetLanguageOption English { get; } = new("en", LanguageDisplayNames.ForIso639("en"));

    public static IReadOnlyList<PipelineTargetLanguageOption> All { get; } = BuildAll();

    private static IReadOnlyList<PipelineTargetLanguageOption> BuildAll()
    {
        var items = NllbLanguageCatalog.IsoCodes
            .Select(code => new PipelineTargetLanguageOption(code, LanguageDisplayNames.ForIso639(code)))
            .ToList();

        items.Sort(static (a, b) =>
            string.Equals(a.Code, "en", StringComparison.OrdinalIgnoreCase)
                ? -1
                : string.Equals(b.Code, "en", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        return items;
    }
}
