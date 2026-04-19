using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Babel.Player.Models;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Resources;
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

        var expectedAuto = Strings.ResourceManager.GetString(
            "SpokenLanguage_AutoDetect",
            LocalizationService.Instance.CurrentCulture);
        Assert.Equal(expectedAuto, SpokenLanguageOption.All[0].DisplayName);
        Assert.Null(SpokenLanguageOption.All[0].Code);
    }

    [Fact]
    public void SupportedUiLanguageCatalog_AlignsWithEmbeddedBatchLanguages()
    {
        Assert.Equal(
            NllbLanguageCatalog.IsoCodes.OrderBy(code => code).ToList(),
            SupportedUiLanguageCatalog.IsoCodes.OrderBy(code => code).ToList());

        Assert.True(SupportedUiLanguageCatalog.IsSupported("EN"));
        Assert.True(SupportedUiLanguageCatalog.IsSupported("de"));
        Assert.True(SupportedUiLanguageCatalog.IsSupported("fr"));
        Assert.True(SupportedUiLanguageCatalog.IsSupported("zh"));
        Assert.False(SupportedUiLanguageCatalog.IsSupported(null));
        Assert.False(SupportedUiLanguageCatalog.IsSupported(""));
    }

    [Fact]
    public void ResolveAppLanguage_FallsBackToEnglishForUnknownLanguages()
    {
        Assert.Equal("en", LocalizationService.ResolveAppLanguage("not-a-code"));
        Assert.Equal("en", LocalizationService.ResolveAppLanguage("xx"));
    }

    [Fact]
    public void ResolveAppLanguage_AcceptsShippingUiLanguages()
    {
        Assert.Equal("en", LocalizationService.ResolveAppLanguage("en"));
        Assert.Equal("de", LocalizationService.ResolveAppLanguage("de"));
        Assert.Equal("de", LocalizationService.ResolveAppLanguage("DE"));
        Assert.Equal("fr", LocalizationService.ResolveAppLanguage("fr"));
        Assert.Equal("ja", LocalizationService.ResolveAppLanguage("ja"));
        Assert.Equal("zh", LocalizationService.ResolveAppLanguage("zh-CN"));
    }

    [Fact]
    public void PipelineTargetLanguageOptions_Source_KeepsEnglishSortCultureBeforeAllAndStableComparer()
    {
        var source = File.ReadAllText(FindRepoFile("Models", "PipelineTargetLanguages.cs"));

        AssertDeclarationOrder(
            source,
            "private static readonly CultureInfo EnglishSortCulture",
            "public static IReadOnlyList<PipelineTargetLanguageOption> All");

        AssertComparerGuardOrder(
            source,
            "if (string.Equals(a.Code, b.Code, StringComparison.OrdinalIgnoreCase))",
            "if (string.Equals(a.Code, \"en\", StringComparison.OrdinalIgnoreCase))");

        Assert.Contains(
            "return string.Compare(a.Code, b.Code, StringComparison.OrdinalIgnoreCase);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SpokenLanguageOptions_Source_KeepsEnglishSortCultureBeforeAll()
    {
        var source = File.ReadAllText(FindRepoFile("Models", "TranscriptionSpokenLanguageOptions.cs"));

        AssertDeclarationOrder(
            source,
            "private static readonly CultureInfo EnglishSortCulture",
            "public static IReadOnlyList<SpokenLanguageOption> All");
    }

    [Fact]
    public void BuildStringsResx_Source_KeepsAutoDetectKeyInCanonicalDictionary()
    {
        var source = File.ReadAllText(FindRepoFile("scripts", "build_strings_resx.py"));

        Assert.Contains(
            "\"SpokenLanguage_AutoDetect\": \"Auto-detect\"",
            source,
            StringComparison.Ordinal);
    }

    private static void AssertDeclarationOrder(string source, string earlier, string later)
    {
        var earlierIndex = source.IndexOf(earlier, StringComparison.Ordinal);
        var laterIndex = source.IndexOf(later, StringComparison.Ordinal);

        Assert.True(earlierIndex >= 0, $"Could not find '{earlier}'.");
        Assert.True(laterIndex >= 0, $"Could not find '{later}'.");
        Assert.True(earlierIndex < laterIndex, $"Expected '{earlier}' before '{later}'.");
    }

    private static void AssertComparerGuardOrder(string source, string earlier, string later)
    {
        var earlierIndex = source.IndexOf(earlier, StringComparison.Ordinal);
        var laterIndex = source.IndexOf(later, StringComparison.Ordinal);

        Assert.True(earlierIndex >= 0, $"Could not find comparer guard '{earlier}'.");
        Assert.True(laterIndex >= 0, $"Could not find comparer branch '{later}'.");
        Assert.True(earlierIndex < laterIndex, $"Expected comparer guard '{earlier}' before '{later}'.");
    }

    private static string FindRepoFile(string directory, string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, directory, fileName);
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{Path.Combine(directory, fileName)}' from '{AppContext.BaseDirectory}'.");
    }
}
