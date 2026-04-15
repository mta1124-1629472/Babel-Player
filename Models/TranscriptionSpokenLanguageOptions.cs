using System;
using System.Collections.Generic;
using System.Linq;
using Babel.Player.Models.LanguageSupport;

namespace Babel.Player.Models;

/// <summary>Optional ASR language hints (Whisper <c>language=</c>). <see cref="Code"/> null means auto-detect.</summary>
/// <remarks>Hints are limited to languages that appear in both <see cref="NllbLanguageCatalog"/> and <see cref="WhisperAsrLanguageCatalog"/>.</remarks>
public sealed record SpokenLanguageOption(string? Code, string DisplayName)
{
    public static IReadOnlyList<SpokenLanguageOption> All { get; } = BuildAll();

    private static IReadOnlyList<SpokenLanguageOption> BuildAll()
    {
        var hints = NllbLanguageCatalog.IsoCodes
            .Where(code => WhisperAsrLanguageCatalog.IsSupportedHint(code))
            .Select(code => new SpokenLanguageOption(code, LanguageDisplayNames.ForIso639(code)))
            .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new[] { new SpokenLanguageOption(null, "Auto-detect") }.Concat(hints).ToList();
    }
}
