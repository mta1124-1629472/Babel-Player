using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Babel.Player.Models.LanguageSupport;

/// <summary>Single source for NLLB / CTranslate2 FLORES-200 ISO keys and tokenizer language tokens.</summary>
public static class NllbLanguageCatalog
{
    /// <summary>ISO 639-1 (or zh) keys supported by embedded local NMT scripts.</summary>
    public static readonly IReadOnlyDictionary<string, string> IsoToFloresToken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "eng_Latn",
        ["es"] = "spa_Latn",
        ["fr"] = "fra_Latn",
        ["de"] = "deu_Latn",
        ["it"] = "ita_Latn",
        ["pt"] = "por_Latn",
        ["ru"] = "rus_Cyrl",
        ["zh"] = "zho_Hans",
        ["ja"] = "jpn_Jpan",
        ["ko"] = "kor_Hang",
        ["ar"] = "arb_Arab",
        ["hi"] = "hin_Deva",
        ["nl"] = "nld_Latn",
        ["pl"] = "pol_Latn",
        ["sv"] = "swe_Latn",
        ["tr"] = "tur_Latn",
    };

    public static IReadOnlyList<string> IsoCodes => IsoToFloresToken.Keys.OrderBy(k => k).ToList();

    /// <summary>Python dict literal <c>FLORES = { ... }</c> body for embedding in subprocess scripts.</summary>
    public static string BuildPythonDictLiteral()
    {
        var sb = new StringBuilder();
        foreach (var kv in IsoToFloresToken.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (sb.Length > 0)
                sb.Append(',');
            var v = kv.Value.Replace("\\", "\\\\").Replace("'", "\\'");
            sb.Append('\'').Append(kv.Key.ToLowerInvariant()).Append("':'").Append(v).Append('\'');
        }

        return sb.ToString();
    }
}
