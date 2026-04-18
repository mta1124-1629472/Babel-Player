using System;
using System.Collections.Generic;
using Babel.Player.Resources;
using Babel.Player.Services;

namespace Babel.Player.Models;

/// <summary>User-facing labels for ISO 639-1 pipeline language codes (aligned with <see cref="LanguageSupport.NllbLanguageCatalog"/>).</summary>
/// <remarks>
/// Display names are sourced from the app's resource files so the label
/// follows the currently selected UI culture.  The hard-coded English
/// fallbacks mirror the <c>Language_xx</c> keys in <c>Resources/Strings.resx</c>
/// and exist so tests and code paths that run before the resource manager is
/// initialized still return a meaningful label.
/// </remarks>
public static class LanguageDisplayNames
{
    private static readonly Dictionary<string, string> EnglishFallbacks = new(StringComparer.OrdinalIgnoreCase)
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

    /// <summary>Returns the display name for an ISO 639-1 code in the current UI culture.</summary>
    public static string ForIso639(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var key = "Language_" + code.Trim().ToLowerInvariant();
        var localized = Strings.ResourceManager.GetString(key, LocalizationService.Instance.CurrentCulture);
        if (!string.IsNullOrEmpty(localized))
            return localized;

        return EnglishFallbacks.TryGetValue(code.Trim(), out var name) ? name : code;
    }
}
