using System.Linq;
using System.Text.RegularExpressions;
using Babel.Player.Models.LanguageSupport;

namespace BabelPlayer.Tests;

public sealed class LanguageCatalogIntegrityTests
{
    [Fact]
    public void NllbLanguageCatalog_IsoKeysMatchPythonDictLiteral()
    {
        var literal = NllbLanguageCatalog.BuildPythonDictLiteral();
        var matches = Regex.Matches(literal, "'([a-z]{2,3})':'[^']+'");
        var keysFromLiteral = matches.Select(m => m.Groups[1].Value).OrderBy(k => k).ToList();
        Assert.Equal(NllbLanguageCatalog.IsoCodes.OrderBy(k => k).ToList(), keysFromLiteral);
    }

    [Fact]
    public void NllbLanguageCatalog_BuildPythonDictLiteral_IsParseableShape()
    {
        var literal = NllbLanguageCatalog.BuildPythonDictLiteral();
        Assert.Contains("'en':'eng_Latn'", literal);
        Assert.DoesNotContain("{", literal);
        Assert.DoesNotContain("}", literal);
    }

    [Fact]
    public void EdgeTtsCatalog_VoiceIdsAreNonEmptyAndUnique() =>
        Assert.Equal(EdgeTtsCatalog.Voices.Count, EdgeTtsCatalog.VoiceIds.Distinct().Count());

    [Fact]
    public void PiperTtsCatalog_VoiceIdsAreNonEmptyAndUnique() =>
        Assert.Equal(PiperTtsCatalog.Voices.Count, PiperTtsCatalog.VoiceIds.Distinct().Count());

    [Fact]
    public void DeepLTranslationCatalog_IncludesCommonNormalizedTargets()
    {
        Assert.True(DeepLTranslationCatalog.IsSupportedApiCode("EN"));
        Assert.True(DeepLTranslationCatalog.IsSupportedApiCode("EN-GB"));
        Assert.True(DeepLTranslationCatalog.IsSupportedApiCode("PT-BR"));
    }

    [Fact]
    public void WhisperAsrLanguageCatalog_IncludesCommonCodes()
    {
        Assert.True(WhisperAsrLanguageCatalog.IsSupportedHint("en"));
        Assert.True(WhisperAsrLanguageCatalog.IsSupportedHint("zh"));
        Assert.False(WhisperAsrLanguageCatalog.IsSupportedHint("not-a-whisper-code-xyz"));
    }
}
