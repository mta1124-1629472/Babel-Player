using System;
using System.Collections.Generic;

namespace Babel.Player.Models.LanguageSupport;

/// <summary>DeepL API v2 language codes after client normalization (uppercase).</summary>
public static class DeepLTranslationCatalog
{
    private static readonly HashSet<string> ApiCodes = new(StringComparer.Ordinal);

    static DeepLTranslationCatalog()
    {
        foreach (var c in
                 "AR BG BN BS CS DA DE EL EN EN-GB EN-US ES ET FI FR HE HI HR HU ID IT JA KO LT LV MS NB NL PL PT PT-BR PT-PT RO RU SK SL SV TH TR UK UR VI ZH"
                     .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            ApiCodes.Add(c);
    }

    public static bool IsSupportedApiCode(string normalizedUpperApiCode) =>
        ApiCodes.Contains(normalizedUpperApiCode.Trim());
}
