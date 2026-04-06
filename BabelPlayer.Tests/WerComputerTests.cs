using Babel.Player.Services;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for <see cref="WerComputer"/> — WER/CER normalization, edit-distance
/// edge cases, and expected scoring semantics.
/// </summary>
public sealed class WerComputerTests
{
    // ── WER — exact match ──────────────────────────────────────────────────────

    [Fact]
    public void ComputeWer_IdenticalStrings_ReturnsZero()
    {
        Assert.Equal(0.0, WerComputer.ComputeWer("hello world", "hello world"));
    }

    [Fact]
    public void ComputeWer_CaseDifference_NormalizesToZero()
    {
        Assert.Equal(0.0, WerComputer.ComputeWer("Hello World", "hello world"));
    }

    [Fact]
    public void ComputeWer_ExtraWhitespace_NormalizesToZero()
    {
        Assert.Equal(0.0, WerComputer.ComputeWer("hello  world", "hello world"));
    }

    // ── WER — empty inputs ─────────────────────────────────────────────────────

    [Fact]
    public void ComputeWer_EmptyReference_ReturnsZero()
    {
        Assert.Equal(0.0, WerComputer.ComputeWer("", "some hypothesis"));
    }

    [Fact]
    public void ComputeWer_EmptyHypothesis_ReturnsOne()
    {
        // 2 deletions / 2 reference words = 1.0
        Assert.Equal(1.0, WerComputer.ComputeWer("hello world", ""));
    }

    [Fact]
    public void ComputeWer_BothEmpty_ReturnsZero()
    {
        Assert.Equal(0.0, WerComputer.ComputeWer("", ""));
    }

    [Fact]
    public void ComputeWer_WhitespaceOnlyReference_ReturnsZero()
    {
        Assert.Equal(0.0, WerComputer.ComputeWer("   ", "hello"));
    }

    // ── WER — substitution / deletion / insertion ──────────────────────────────

    [Fact]
    public void ComputeWer_OneSubstitution_ReturnsCorrectRate()
    {
        // ref: "the cat sat", hyp: "the dog sat" — 1 substitution / 3 words
        var wer = WerComputer.ComputeWer("the cat sat", "the dog sat");
        Assert.Equal(1.0 / 3.0, wer, precision: 10);
    }

    [Fact]
    public void ComputeWer_OneDeletion_ReturnsCorrectRate()
    {
        // ref: "the cat sat", hyp: "the sat" — 1 deletion / 3 words
        var wer = WerComputer.ComputeWer("the cat sat", "the sat");
        Assert.Equal(1.0 / 3.0, wer, precision: 10);
    }

    [Fact]
    public void ComputeWer_InsertionHeavyHypothesis_CanExceedOne()
    {
        // ref: "a", hyp: "a b c d e" — 4 insertions / 1 word = 4.0
        var wer = WerComputer.ComputeWer("a", "a b c d e");
        Assert.Equal(4.0, wer, precision: 10);
    }

    [Fact]
    public void ComputeWer_CompletelyDifferent_ReturnsOneOrMore()
    {
        // ref: "hello world", hyp: "foo bar" — 2 substitutions / 2 words = 1.0
        var wer = WerComputer.ComputeWer("hello world", "foo bar");
        Assert.Equal(1.0, wer, precision: 10);
    }

    // ── WER — Unicode / diacritics ─────────────────────────────────────────────

    [Fact]
    public void ComputeWer_UnicodeInput_PerfectMatch_ReturnsZero()
    {
        Assert.Equal(0.0, WerComputer.ComputeWer("¿cómo estás?", "¿cómo estás?"));
    }

    [Fact]
    public void ComputeWer_DiacriticMismatch_CountsAsOneSubstitution()
    {
        // "café" vs "cafe" are different tokens (no diacritic stripping)
        var wer = WerComputer.ComputeWer("café", "cafe");
        Assert.True(wer > 0.0, "diacritic mismatch should produce WER > 0");
    }

    // ── CER — basic cases ──────────────────────────────────────────────────────

    [Fact]
    public void ComputeCer_IdenticalStrings_ReturnsZero()
    {
        Assert.Equal(0.0, WerComputer.ComputeCer("hello", "hello"));
    }

    [Fact]
    public void ComputeCer_EmptyReference_ReturnsZero()
    {
        Assert.Equal(0.0, WerComputer.ComputeCer("", "hypothesis"));
    }

    [Fact]
    public void ComputeCer_EmptyHypothesis_ReturnsOne()
    {
        // 5 deletions / 5 ref chars = 1.0
        Assert.Equal(1.0, WerComputer.ComputeCer("hello", ""));
    }

    [Fact]
    public void ComputeCer_SingleCharSubstitution_ReturnsCorrectRate()
    {
        // "hello" vs "hxllo" — 1 substitution / 5 chars = 0.2
        var cer = WerComputer.ComputeCer("hello", "hxllo");
        Assert.Equal(0.2, cer, precision: 10);
    }

    [Fact]
    public void ComputeCer_CaseDifference_NormalizesToZero()
    {
        Assert.Equal(0.0, WerComputer.ComputeCer("Hello", "hello"));
    }

    [Fact]
    public void ComputeCer_BothEmpty_ReturnsZero()
    {
        Assert.Equal(0.0, WerComputer.ComputeCer("", ""));
    }
}
