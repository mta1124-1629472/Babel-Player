using System;
using System.Text.RegularExpressions;

namespace Babel.Player.Models.LanguageSupport;

/// <summary>Google Cloud Speech-to-Text accepts a large BCP-47 set; we validate shape only.</summary>
public static partial class GoogleSttLanguageCatalog
{
    [GeneratedRegex("^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex Bcp47Shape();

    public static bool IsProbablyValidLanguageTag(string? tag) =>
        !string.IsNullOrWhiteSpace(tag) && Bcp47Shape().IsMatch(tag.Trim());
}
