using System.Collections.Generic;
using System.Linq;

namespace Babel.Player.Models.LanguageSupport;

public sealed record EdgeTtsVoiceRow(string VoiceId, string CanonicalCode, string? DisplayName = null);

public static class EdgeTtsCatalog
{
    public static readonly IReadOnlyList<EdgeTtsVoiceRow> Voices =
    [
        new("en-US-AriaNeural", "en", "Aria (US)"),
        new("en-US-GuyNeural", "en", "Guy (US)"),
        new("en-US-JennyNeural", "en", "Jenny (US)"),
        new("en-US-ChristopherNeural", "en", "Christopher (US)"),
        new("en-GB-SoniaNeural", "en", "Sonia (GB)"),
        new("en-GB-RyanNeural", "en", "Ryan (GB)"),
        new("en-AU-NatashaNeural", "en", "Natasha (AU)"),
        new("en-AU-WilliamNeural", "en", "William (AU)"),
        new("es-ES-ElviraNeural", "es", "Elvira"),
        new("es-ES-AlvaroNeural", "es", "Alvaro"),
        new("fr-FR-DeniseNeural", "fr", "Denise"),
        new("fr-FR-HenriNeural", "fr", "Henri"),
        new("de-DE-KatjaNeural", "de", "Katja"),
        new("de-DE-ConradNeural", "de", "Conrad"),
        new("it-IT-ElsaNeural", "it", "Elsa"),
        new("it-IT-DiegoNeural", "it", "Diego"),
        new("pt-BR-FranciscaNeural", "pt", "Francisca"),
        new("pt-BR-AntonioNeural", "pt", "Antonio"),
        new("ja-JP-NanamiNeural", "ja", "Nanami"),
        new("ja-JP-KeitaNeural", "ja", "Keita"),
        new("ko-KR-SunHiNeural", "ko", "Sun-Hi"),
        new("ko-KR-InJoonNeural", "ko", "InJoon"),
        new("zh-CN-XiaoxiaoNeural", "zh", "Xiaoxiao"),
        new("zh-CN-YunxiNeural", "zh", "Yunxi"),
        new("ar-SA-ZariyahNeural", "ar", "Zariyah"),
        new("ar-SA-HamedNeural", "ar", "Hamed"),
        new("hi-IN-SwaraNeural", "hi", "Swara"),
        new("hi-IN-MadhurNeural", "hi", "Madhur"),
        new("ru-RU-SvetlanaNeural", "ru", "Svetlana"),
        new("ru-RU-DmitryNeural", "ru", "Dmitry"),
    ];

    public static IReadOnlyList<string> VoiceIds { get; } = Voices.Select(v => v.VoiceId).ToList();
}
