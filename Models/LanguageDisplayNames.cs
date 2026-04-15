using System;
using System.Collections.Generic;

namespace Babel.Player.Models;

/// <summary>User-facing labels for ISO 639-1 pipeline language codes (aligned with <see cref="LanguageSupport.NllbLanguageCatalog"/>).</summary>
public static class LanguageDisplayNames
{
    private static readonly Dictionary<string, string> Iso639 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = "Arabic",
        ["de"] = "German",
        ["en"] = "English",
        ["es"] = "Spanish",
        ["fr"] = "French",
        ["hi"] = "Hindi",
        ["it"] = "Italian",
        ["ja"] = "Japanese",
        ["ko"] = "Korean",
        ["nl"] = "Dutch",
        ["pl"] = "Polish",
        ["pt"] = "Portuguese",
        ["ru"] = "Russian",
        ["sv"] = "Swedish",
        ["tr"] = "Turkish",
        ["zh"] = "Chinese (Simplified)",
    };

    public static string ForIso639(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;
        return Iso639.TryGetValue(code.Trim(), out var name) ? name : code;
    }
}
