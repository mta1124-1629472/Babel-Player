using System;
using System.Collections.Generic;

namespace Babel.Player.Models.LanguageSupport;

/// <summary>
/// Whisper / faster-whisper ASR language ids (ISO 639-1 primary or Whisper-specific ids).
/// Synced with OpenAI Whisper tokenizer language ids (see whisper/tokenizer.py <c>LANGUAGES</c>).
/// </summary>
public static class WhisperAsrLanguageCatalog
{
    private static readonly HashSet<string> Codes = new(StringComparer.OrdinalIgnoreCase);

    static WhisperAsrLanguageCatalog()
    {
        // Space-separated Whisper <c>LANGUAGES</c> keys (OpenAI whisper repo, tokenizer.py).
        const string joined =
            "af ar az ba be bg bn bo br bs ca cs cy da de el en es et eu fa fi fo fr gl gu ha haw he hi hr ht hu hy id is it ja jw ka kk km kn ko la lb ln lo lt lv mg mi mk ml mn mr ms mt my ne nl nn no oc pa pl ps pt ro ru sa sd si sk sl sn so sq sr su sv sw ta te tg th tk tl tr tt uk ur uz vi yi yo zh";
        foreach (var p in joined.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Codes.Add(p);
    }

    public static bool IsSupportedHint(string? normalizedPrimarySubtag)
    {
        var n = LanguageCode.NormalizeForPersistence(normalizedPrimarySubtag);
        return n is not null && Codes.Contains(n);
    }
}
