using System.Collections.Generic;
using Babel.Player.Models;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class SrtGeneratorTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static WorkflowSegmentState Seg(
        double start, double end,
        string source, string? translated = null) =>
        new("id", start, end, source, translated is not null, translated, false);

    // ── Empty / trivial input ──────────────────────────────────────────────────

    [Fact]
    public void Generate_EmptyList_ReturnsEmptyString()
    {
        var result = SrtGenerator.Generate([]);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Generate_AllSegmentsHaveWhitespaceText_ReturnsEmptyString()
    {
        var segments = new[]
        {
            Seg(0, 1, "   "),
            Seg(1, 2, "\t\n"),
        };
        var result = SrtGenerator.Generate(segments);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Generate_SegmentWithNullBothTexts_IsSkipped()
    {
        // SourceText is non-nullable in the record, but TranslatedText can be null.
        // A whitespace SourceText with null TranslatedText should be skipped.
        var seg = new WorkflowSegmentState("id", 0, 1, " ", false, null, false);
        var result = SrtGenerator.Generate([seg]);
        Assert.Equal(string.Empty, result);
    }

    // ── Index numbering ────────────────────────────────────────────────────────

    [Fact]
    public void Generate_MultipleSegments_AreNumberedSequentially()
    {
        var segments = new[]
        {
            Seg(0, 1, "First"),
            Seg(1, 2, "Second"),
            Seg(2, 3, "Third"),
        };
        var result = SrtGenerator.Generate(segments);

        Assert.Contains("\n1\n", "\n" + result);
        Assert.Contains("\n2\n", "\n" + result);
        Assert.Contains("\n3\n", "\n" + result);
    }

    [Fact]
    public void Generate_SkippedSegmentsDoNotBreakNumbering()
    {
        // Middle segment has blank text — numbering should jump 1→2, not 1→2→3.
        var segments = new[]
        {
            Seg(0, 1, "First"),
            Seg(1, 2, "   "),   // skipped
            Seg(2, 3, "Third"),
        };
        var lines = SrtGenerator.Generate(segments).Split('\n');
        // First non-empty line is "1", third block starts with "2"
        Assert.Equal("1", lines[0]);
        // Find next index line
        var indexLines = new List<string>();
        foreach (var line in lines)
            if (int.TryParse(line.Trim(), out _))
                indexLines.Add(line.Trim());

        Assert.Equal(new[] { "1", "2" }, indexLines);
    }

    // ── Translation preference ─────────────────────────────────────────────────

    [Fact]
    public void Generate_PrefersTranslatedTextOverSourceText()
    {
        var seg = Seg(0, 1, "Original", "Translated");
        var result = SrtGenerator.Generate([seg]);
        Assert.Contains("Translated", result);
        Assert.DoesNotContain("Original", result);
    }

    [Fact]
    public void Generate_FallsBackToSourceTextWhenNoTranslation()
    {
        var seg = Seg(0, 1, "Source only");
        var result = SrtGenerator.Generate([seg]);
        Assert.Contains("Source only", result);
    }

    [Fact]
    public void Generate_NullTranslatedText_FallsBackToSourceText()
    {
        var seg = new WorkflowSegmentState("id", 0, 1, "Source", true, null, false);
        var result = SrtGenerator.Generate([seg]);
        Assert.Contains("Source", result);
    }

    // ── Text trimming ──────────────────────────────────────────────────────────

    [Fact]
    public void Generate_TrimsLeadingAndTrailingWhitespaceFromText()
    {
        var seg = Seg(0, 1, "  hello world  ");
        var result = SrtGenerator.Generate([seg]);
        Assert.Contains("hello world", result);
        // Ensure there's no double-space or leading/trailing space on that line
        var textLine = result.Split('\n')[2]; // index, timestamp, text
        Assert.Equal("hello world", textLine);
    }

    // ── Timestamp formatting ───────────────────────────────────────────────────

    [Fact]
    public void Generate_SubMinuteTimestamp_FormatsCorrectly()
    {
        var seg = Seg(5.123, 9.999, "Text");
        var result = SrtGenerator.Generate([seg]);
        Assert.Contains("00:00:05,123 --> 00:00:09,999", result);
    }

    [Fact]
    public void Generate_SubHourTimestamp_FormatsCorrectly()
    {
        var seg = Seg(90.0, 125.5, "Text"); // 1:30.000 → 2:05.500
        var result = SrtGenerator.Generate([seg]);
        Assert.Contains("00:01:30,000 --> 00:02:05,500", result);
    }

    [Fact]
    public void Generate_OverOneHourTimestamp_FormatsCorrectly()
    {
        var seg = Seg(3661.0, 3723.456, "Text"); // 1:01:01.000 → 1:02:03.456
        var result = SrtGenerator.Generate([seg]);
        Assert.Contains("01:01:01,000 --> 01:02:03,456", result);
    }

    [Fact]
    public void Generate_ZeroTimestamp_FormatsCorrectly()
    {
        var seg = Seg(0, 0, "Text");
        var result = SrtGenerator.Generate([seg]);
        Assert.Contains("00:00:00,000 --> 00:00:00,000", result);
    }

    [Fact]
    public void Generate_MillisecondPrecision_IsTruncatedNotRounded()
    {
        // 1.9999 seconds — milliseconds should be 999, not rounded to 1000
        var seg = Seg(1.9999, 2.0, "Text");
        var result = SrtGenerator.Generate([seg]);
        // TimeSpan.FromSeconds(1.9999).Milliseconds == 999
        Assert.Contains("00:00:01,999", result);
    }

    // ── SRT block structure ────────────────────────────────────────────────────

    [Fact]
    public void Generate_SingleSegment_HasCorrectBlockStructure()
    {
        var seg = Seg(1.0, 2.0, "Hello");
        var result = SrtGenerator.Generate([seg]);
        // Expected:
        // 1\r\n (or \n)
        // 00:00:01,000 --> 00:00:02,000\r\n
        // Hello\r\n
        // \r\n
        var lines = result.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("1", lines[0]);
        Assert.Equal("00:00:01,000 --> 00:00:02,000", lines[1]);
        Assert.Equal("Hello", lines[2]);
        Assert.Equal(string.Empty, lines[3]); // blank separator line
    }

    [Fact]
    public void Generate_MultipleSegments_EachBlockSeparatedByBlankLine()
    {
        var segments = new[]
        {
            Seg(0, 1, "A"),
            Seg(1, 2, "B"),
        };
        var result = SrtGenerator.Generate(segments);
        var lines = result.Replace("\r\n", "\n").Split('\n');
        // Block 1: lines 0-3 (index, timestamp, text, blank)
        // Block 2: lines 4-7
        Assert.Equal("1", lines[0]);
        Assert.Equal(string.Empty, lines[3]);
        Assert.Equal("2", lines[4]);
        Assert.Equal(string.Empty, lines[7]);
    }
}
