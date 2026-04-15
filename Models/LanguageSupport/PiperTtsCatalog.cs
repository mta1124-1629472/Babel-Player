using System.Collections.Generic;
using System.Linq;

namespace Babel.Player.Models.LanguageSupport;

public sealed record PiperVoiceRow(string VoiceId, string CanonicalCode, string? DisplayName = null);

/// <summary>
/// Curated Piper (rhasspy/piper-voices v1.0.0) models aligned with <see cref="NllbLanguageCatalog"/> ISO codes.
/// <see cref="NllbIsoCodesWithoutRhasspyVoice"/> lists NLLB targets that have no Piper release in that corpus (use Edge or cloud TTS).
/// </summary>
public static class PiperTtsCatalog
{
    /// <summary>ISO 639-1 codes present in <see cref="NllbLanguageCatalog"/> but without a voice under rhasspy/piper-voices v1.0.0.</summary>
    public static readonly IReadOnlyList<string> NllbIsoCodesWithoutRhasspyVoice =
    [
        "ja",
        "ko",
    ];

    /// <summary>
    /// Recommended voices for download UX and TTS registry. <see cref="PiperVoiceRow.CanonicalCode"/> matches NLLB / pipeline language keys.
    /// </summary>
    public static readonly IReadOnlyList<PiperVoiceRow> Voices =
    [
        new("ar_JO-kareem-medium", "ar", "Kareem (JO)"),
        new("de_DE-thorsten-medium", "de", "Thorsten"),
        new("en_US-lessac-medium", "en", "Lessac (US)"),
        new("en_US-ryan-high", "en", "Ryan (US)"),
        new("en_US-ljspeech-high", "en", "LJSpeech (US)"),
        new("en_GB-alan-medium", "en", "Alan (GB)"),
        new("es_ES-mls_10246-low", "es", "MLS 10246 (ES)"),
        new("fr_FR-gilles-low", "fr", "Gilles"),
        new("hi_IN-pratham-medium", "hi", "Pratham"),
        new("it_IT-paola-medium", "it", "Paola"),
        new("nl_NL-pim-medium", "nl", "Pim"),
        new("pl_PL-gosia-medium", "pl", "Gosia"),
        new("pt_BR-edresson-low", "pt", "Edresson (BR)"),
        new("ru_RU-denis-medium", "ru", "Denis"),
        new("sv_SE-lisa-medium", "sv", "Lisa"),
        new("tr_TR-fettah-medium", "tr", "Fettah"),
        new("zh_CN-huayan-medium", "zh", "Huayan (CN)"),
    ];

    public static IReadOnlyList<string> VoiceIds { get; } = Voices.Select(v => v.VoiceId).ToList();
}
