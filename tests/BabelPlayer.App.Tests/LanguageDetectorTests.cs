using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

public sealed class LanguageDetectorTests
{
    [Fact]
    public void Detect_ReturnsEnglish_WhenInputIsEmpty()
    {
        Assert.Equal("en", LanguageDetector.Detect(string.Empty));
        Assert.Equal("en", LanguageDetector.Detect("   "));
    }

    [Fact]
    public void Detect_ReturnsZh_ForChineseCharacters()
    {
        // CJK Unified Ideographs
        var result = LanguageDetector.Detect("你好世界");

        Assert.Equal("zh", result);
    }

    [Fact]
    public void Detect_ReturnsJa_ForJapaneseHiragana()
    {
        // Hiragana block
        var result = LanguageDetector.Detect("こんにちは");

        Assert.Equal("ja", result);
    }

    [Fact]
    public void Detect_ReturnsJa_ForJapaneseKatakana()
    {
        // Katakana block
        var result = LanguageDetector.Detect("コンピュータ");

        Assert.Equal("ja", result);
    }

    [Fact]
    public void Detect_ReturnsRu_ForCyrillicScript()
    {
        var result = LanguageDetector.Detect("Привет мир");

        Assert.Equal("ru", result);
    }

    [Fact]
    public void Detect_ReturnsAr_ForArabicScript()
    {
        var result = LanguageDetector.Detect("مرحبا بالعالم");

        Assert.Equal("ar", result);
    }

    [Fact]
    public void Detect_ReturnsEn_WhenEnglishHintsArePresent()
    {
        var result = LanguageDetector.Detect("This is the best way to do it");

        Assert.Equal("en", result);
    }

    [Fact]
    public void Detect_ReturnsEs_WhenSpanishHintsArePresent()
    {
        var result = LanguageDetector.Detect("Hola, que pasa para manana");

        Assert.Equal("es", result);
    }

    [Fact]
    public void Detect_ReturnsFr_WhenFrenchHintsArePresent()
    {
        var result = LanguageDetector.Detect("Bonjour avec vous merci");

        Assert.Equal("fr", result);
    }

    [Fact]
    public void Detect_ReturnsEn_ForPureAsciiWithoutHints()
    {
        var result = LanguageDetector.Detect("ABCDEFGH nohints");

        Assert.Equal("en", result);
    }

    [Fact]
    public void Detect_ReturnsUnd_ForNonAsciiWithoutCjkOrKnownHints()
    {
        // Latin extended characters that are non-ASCII but not CJK, Cyrillic, or Arabic
        var result = LanguageDetector.Detect("Ñoño über bröt");

        Assert.Equal("und", result);
    }

    [Fact]
    public void Detect_TruncatesLongInput()
    {
        // Very long string with Japanese at the start (should still detect ja)
        var longInput = "こ" + new string('x', 500);

        var result = LanguageDetector.Detect(longInput);

        Assert.Equal("ja", result);
    }
}
