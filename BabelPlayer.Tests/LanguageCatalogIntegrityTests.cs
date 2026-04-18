using System.Linq;
using System.Text.RegularExpressions;
using Babel.Player.Models;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Services;

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
    public void PiperVoiceRows_UseNllbCanonicalCodes()
    {
        var nllb = NllbLanguageCatalog.IsoCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in PiperTtsCatalog.Voices)
            Assert.Contains(row.CanonicalCode, nllb);
    }

    [Fact]
    public void PiperTtsCatalog_OffersVoiceForEachNllbLanguageExceptUnpublished()
    {
        var covered = PiperTtsCatalog.Voices
            .Select(v => v.CanonicalCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skip = PiperTtsCatalog.NllbIsoCodesWithoutRhasspyVoice
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var code in NllbLanguageCatalog.IsoCodes)
        {
            if (skip.Contains(code))
                continue;
            Assert.Contains(code, covered);
        }
    }

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

    [Fact]
    public void PipelineTargetLanguageOptions_AlignWithNllbCatalog()
    {
        var targets = PipelineTargetLanguageOption.All.Select(o => o.Code).OrderBy(c => c).ToList();
        Assert.Equal(NllbLanguageCatalog.IsoCodes.OrderBy(c => c).ToList(), targets);
    }

    [Fact]
    public void SpokenLanguageOptions_CoverNllbCodesSupportedByWhisper()
    {
        var hints = SpokenLanguageOption.All.Where(o => o.Code is not null).Select(o => o.Code!).ToHashSet();
        foreach (var code in NllbLanguageCatalog.IsoCodes)
        {
            if (WhisperAsrLanguageCatalog.IsSupportedHint(code))
                Assert.Contains(code, hints);
        }

        Assert.Equal("Auto-detect", SpokenLanguageOption.All[0].DisplayName);
        Assert.Null(SpokenLanguageOption.All[0].Code);
    }

    [Fact]
    public void SupportedUiLanguageCatalog_IncludesBaseEnglishAndGermanSatellite()
    {
        Assert.Contains("en", SupportedUiLanguageCatalog.IsoCodes);
        Assert.Contains("de", SupportedUiLanguageCatalog.IsoCodes);
        Assert.True(SupportedUiLanguageCatalog.IsSupported("EN"));
        Assert.True(SupportedUiLanguageCatalog.IsSupported("de"));
        Assert.False(SupportedUiLanguageCatalog.IsSupported("fr"));
        Assert.False(SupportedUiLanguageCatalog.IsSupported(null));
        Assert.False(SupportedUiLanguageCatalog.IsSupported(""));
    }

    [Fact]
    public void ResolveAppLanguage_FallsBackToEnglishForPipelineOnlyLanguages()
    {
        // Languages in the NLLB pipeline catalog that have no shipping Strings.*.resx
        // must not be treated as valid UI languages — picking them would silently fall
        // back to English strings with no user feedback.
        Assert.Equal("en", LocalizationService.ResolveAppLanguage("fr"));
        Assert.Equal("en", LocalizationService.ResolveAppLanguage("ja"));
        Assert.Equal("en", LocalizationService.ResolveAppLanguage("zh"));
        Assert.Equal("en", LocalizationService.ResolveAppLanguage("not-a-code"));
    }

    [Fact]
    public void ResolveAppLanguage_AcceptsShippingUiLanguages()
    {
        Assert.Equal("en", LocalizationService.ResolveAppLanguage("en"));
        Assert.Equal("de", LocalizationService.ResolveAppLanguage("de"));
        Assert.Equal("de", LocalizationService.ResolveAppLanguage("DE"));
    }
}
