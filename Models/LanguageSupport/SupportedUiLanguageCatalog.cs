using System;
using System.Collections.Generic;
using System.Linq;

namespace Babel.Player.Models.LanguageSupport;

/// <summary>
/// ISO 639-1 codes for which the app ships a real UI translation
/// (either the base <c>Resources/Strings.resx</c> or a satellite
/// <c>Resources/Strings.&lt;code&gt;.resx</c>).
/// </summary>
/// <remarks>
/// This is intentionally a separate, smaller set than
/// <see cref="NllbLanguageCatalog"/>: that catalog enumerates languages
/// supported by the translation pipeline, while this catalog enumerates
/// languages whose UI strings have been translated.  The app-language
/// dropdown and <c>ResolveAppLanguage</c> validation gate against this
/// list so selecting a pipeline-only language does not silently fall
/// back to English strings without user feedback.
///
/// Add a code here once <c>Resources/Strings.&lt;code&gt;.resx</c> is
/// landed and reviewed.
/// </remarks>
public static class SupportedUiLanguageCatalog
{
    /// <summary>Shipping UI languages in app-language dropdown order (not ISO or English-first).</summary>
    public static readonly IReadOnlyList<string> IsoCodes = new[]
    {
        "ar",
        "de",
        "en",
        "es",
        "fr",
        "hi",
        "it",
        "ja",
        "ko",
        "nl",
        "pl",
        "pt",
        "ru",
        "sv",
        "tr",
        "zh",
    };

    /// <summary>True when <paramref name="code"/> has a shipping UI translation.</summary>
    public static bool IsSupported(string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        IsoCodes.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
}
