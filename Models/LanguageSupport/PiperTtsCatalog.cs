using System.Collections.Generic;
using System.Linq;

namespace Babel.Player.Models.LanguageSupport;

public sealed record PiperVoiceRow(string VoiceId, string CanonicalCode, string? DisplayName = null);

public static class PiperTtsCatalog
{
    public static readonly IReadOnlyList<PiperVoiceRow> Voices =
    [
        new("en_US-lessac-medium", "en", "Lessac (US)"),
        new("en_US-ryan-high", "en", "Ryan (US)"),
        new("en_US-ljspeech-high", "en", "LJSpeech (US)"),
        new("en_GB-alan-medium", "en", "Alan (GB)"),
        new("de_DE-thorsten-medium", "de", "Thorsten"),
        new("fr_FR-gilles-low", "fr", "Gilles"),
        new("es_ES-mls_10246-low", "es", "MLS ES"),
    ];

    public static IReadOnlyList<string> VoiceIds { get; } = Voices.Select(v => v.VoiceId).ToList();
}
