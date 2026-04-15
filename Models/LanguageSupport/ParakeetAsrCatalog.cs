namespace Babel.Player.Models.LanguageSupport;

/// <summary>Parakeet TDT in this repo is English-oriented; only <c>en</c> is treated as a validated ASR hint.</summary>
public static class ParakeetAsrCatalog
{
    public const string SupportedIso = "en";

    public static bool IsSupportedHint(string? normalizedIso) =>
        string.Equals(normalizedIso, SupportedIso, StringComparison.OrdinalIgnoreCase);
}
