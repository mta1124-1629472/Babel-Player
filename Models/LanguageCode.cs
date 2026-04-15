using System;
using System.Collections.Generic;

namespace Babel.Player.Models;

/// <summary>
/// Canonical pipeline language codes for settings, snapshots, and artifacts (typically lowercase ISO 639-1 primary subtags).
/// Regional variants are collapsed for NLLB-style keys; vendor-specific forms are produced only in provider adapters.
/// </summary>
public static class LanguageCode
{
    private static readonly Dictionary<string, string> DisplayNameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en",
        ["spanish"] = "es",
        ["french"] = "fr",
        ["german"] = "de",
        ["italian"] = "it",
        ["portuguese"] = "pt",
        ["russian"] = "ru",
        ["chinese"] = "zh",
        ["japanese"] = "ja",
        ["korean"] = "ko",
        ["arabic"] = "ar",
        ["hindi"] = "hi",
        ["dutch"] = "nl",
        ["polish"] = "pl",
        ["swedish"] = "sv",
        ["turkish"] = "tr",
    };

    /// <summary>Normalizes user/provider input for persistence and comparison.</summary>
    /// <remarks>
    /// Trims; maps <c>auto</c> (case-insensitive) to null; collapses common BCP-47 tags to the primary subtag
    /// (e.g. <c>en-US</c> → <c>en</c>, <c>pt-BR</c> → <c>pt</c>); lowercases ASCII letters.
    /// </remarks>
    public static string? NormalizeForPersistence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;

        if (DisplayNameAliases.TryGetValue(trimmed, out var alias))
            trimmed = alias;

        trimmed = trimmed.Replace('_', '-');

        // Primary language subtag for tags like xx-YY (region/script handled by dropping tail for pipeline keys).
        var dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0)
        {
            var primary = trimmed[..dash];
            if (IsAsciiAlpha(primary) && primary.Length is >= 2 and <= 8)
                trimmed = CollapsePrimaryLanguage(primary);
        }
        else if (trimmed.Length >= 2)
            trimmed = CollapsePrimaryLanguage(trimmed);

        if (trimmed.Length == 0)
            return null;

        return trimmed.ToLowerInvariant();
    }

    /// <summary>True when both values normalize to the same canonical string (or both null).</summary>
    public static bool LanguageEquals(string? a, string? b) =>
        string.Equals(NormalizeForPersistence(a), NormalizeForPersistence(b), StringComparison.Ordinal);

    /// <summary>Compare persisted target language fields (settings vs snapshot).</summary>
    public static bool TargetLanguagesMatch(string? a, string? b) => LanguageEquals(a, b);

    private static string CollapsePrimaryLanguage(string primary)
    {
        // Whisper / ISO primary: lower-case 2–3 letters is typical; keep script variants like zh-Hans as zh for NLLB FLORES.
        if (primary.Length >= 4 && primary.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
            return "zh";

        if (primary.Length is >= 2 and <= 3 && IsAsciiAlpha(primary))
            return primary.ToLowerInvariant();

        return primary.ToLowerInvariant();
    }

    private static bool IsAsciiAlpha(string s)
    {
        foreach (var c in s)
        {
            if (c is < 'A' or > 'z' || (c > 'Z' && c < 'a'))
                return false;
        }

        return s.Length > 0;
    }
}
