using Babel.Player.Models;

namespace BabelPlayer.Tests;

public sealed class LanguageCodeTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData(" en-US ", "en")]
    [InlineData("pt-BR", "pt")]
    [InlineData("english", "en")]
    public void NormalizeForPersistence_CollapsesAsExpected(string? input, string? expected) =>
        Assert.Equal(expected, LanguageCode.NormalizeForPersistence(input));

    [Fact]
    public void NormalizeForPersistence_AutoAndWhitespace_ReturnNull()
    {
        Assert.Null(LanguageCode.NormalizeForPersistence("auto"));
        Assert.Null(LanguageCode.NormalizeForPersistence("  "));
        Assert.Null(LanguageCode.NormalizeForPersistence(""));
    }

    [Fact]
    public void LanguageEquals_IgnoresCasingAndRegion() =>
        Assert.True(LanguageCode.LanguageEquals("EN-us", "en"));

    [Fact]
    public void TargetLanguagesMatch_TreatsNullAndAutoAsEmpty() =>
        Assert.True(LanguageCode.TargetLanguagesMatch(null, "auto"));
}
