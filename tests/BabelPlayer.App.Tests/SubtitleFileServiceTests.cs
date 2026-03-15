using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

public sealed class SubtitleFileServiceTests
{
    // ── ParseSrt ──────────────────────────────────────────────────────────────

    [Fact]
    public void ParseSrt_ReturnsEmpty_WhenFileDoesNotExist()
    {
        var result = SubtitleFileService.ParseSrt("nonexistent_file_abc123.srt");

        Assert.Empty(result);
    }

    [Fact]
    public void ParseSrt_ParsesStandardSrtFormat()
    {
        var path = WriteTempSrt("""
1
00:00:01,000 --> 00:00:03,500
Hello, world!

2
00:00:05,000 --> 00:00:07,000
Second line.
""");
        try
        {
            var cues = SubtitleFileService.ParseSrt(path);

            Assert.Equal(2, cues.Count);
            Assert.Equal(TimeSpan.FromSeconds(1), cues[0].Start);
            Assert.Equal(TimeSpan.FromMilliseconds(3500), cues[0].End);
            Assert.Equal("Hello, world!", cues[0].SourceText);
            Assert.Equal(TimeSpan.FromSeconds(5), cues[1].Start);
            Assert.Equal(TimeSpan.FromSeconds(7), cues[1].End);
            Assert.Equal("Second line.", cues[1].SourceText);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseSrt_HandlesCrlfLineEndings()
    {
        var content = "1\r\n00:00:00,000 --> 00:00:02,000\r\nCRLF line\r\n\r\n";
        var path = Path.GetTempFileName() + ".srt";
        File.WriteAllText(path, content);
        try
        {
            var cues = SubtitleFileService.ParseSrt(path);

            Assert.Single(cues);
            Assert.Equal("CRLF line", cues[0].SourceText);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseSrt_AllowsTimelineAsFirstLine_WhenIndexIsMissing()
    {
        var path = WriteTempSrt("""
00:00:01,000 --> 00:00:02,000
No index block

""");
        try
        {
            var cues = SubtitleFileService.ParseSrt(path);

            Assert.Single(cues);
            Assert.Equal("No index block", cues[0].SourceText);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseSrt_SkipsBlocksWithMalformedTimestamps()
    {
        var path = WriteTempSrt("""
1
00:00:01,000 --> BADTIME
Should be skipped.

2
00:00:05,000 --> 00:00:07,000
Should be kept.
""");
        try
        {
            var cues = SubtitleFileService.ParseSrt(path);

            Assert.Single(cues);
            Assert.Equal("Should be kept.", cues[0].SourceText);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseSrt_SkipsBlocksWithEmptyText()
    {
        var path = WriteTempSrt("""
1
00:00:01,000 --> 00:00:03,000


2
00:00:05,000 --> 00:00:07,000
Valid text.
""");
        try
        {
            var cues = SubtitleFileService.ParseSrt(path);

            Assert.Single(cues);
            Assert.Equal("Valid text.", cues[0].SourceText);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseSrt_ParsesMultiLineCueText()
    {
        var path = WriteTempSrt("""
1
00:00:01,000 --> 00:00:03,000
Line one
Line two
""");
        try
        {
            var cues = SubtitleFileService.ParseSrt(path);

            Assert.Single(cues);
            Assert.Contains("Line one", cues[0].SourceText);
            Assert.Contains("Line two", cues[0].SourceText);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseSrt_AcceptsCommaDecimalSeparatorInTimestamps()
    {
        var path = WriteTempSrt("""
1
00:00:01,500 --> 00:00:02,750
Comma separators.
""");
        try
        {
            var cues = SubtitleFileService.ParseSrt(path);

            Assert.Single(cues);
            Assert.Equal(TimeSpan.FromMilliseconds(1500), cues[0].Start);
            Assert.Equal(TimeSpan.FromMilliseconds(2750), cues[0].End);
        }
        finally { File.Delete(path); }
    }

    // ── ExportSrt ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExportSrt_RoundTripsSourceText()
    {
        var cues = new List<SubtitleCue>
        {
            new() { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(3), SourceText = "Hello" },
            new() { Start = TimeSpan.FromSeconds(5), End = TimeSpan.FromSeconds(7), SourceText = "World" }
        };

        var path = Path.GetTempFileName() + ".srt";
        try
        {
            SubtitleFileService.ExportSrt(path, cues);
            var reparsed = SubtitleFileService.ParseSrt(path);

            Assert.Equal(2, reparsed.Count);
            Assert.Equal("Hello", reparsed[0].SourceText);
            Assert.Equal("World", reparsed[1].SourceText);
            Assert.Equal(TimeSpan.FromSeconds(1), reparsed[0].Start);
            Assert.Equal(TimeSpan.FromSeconds(5), reparsed[1].Start);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExportSrt_UsesTranslatedTextWhenAvailable()
    {
        var cues = new List<SubtitleCue>
        {
            new() { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), SourceText = "Hola", TranslatedText = "Hello" }
        };

        var path = Path.GetTempFileName() + ".srt";
        try
        {
            SubtitleFileService.ExportSrt(path, cues);
            var content = File.ReadAllText(path);

            Assert.Contains("Hello", content);
            Assert.DoesNotContain("Hola", content);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExportSrt_SkipsCuesWithNoText()
    {
        var cues = new List<SubtitleCue>
        {
            new() { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), SourceText = string.Empty }
        };

        var path = Path.GetTempFileName() + ".srt";
        try
        {
            SubtitleFileService.ExportSrt(path, cues);
            var content = File.ReadAllText(path).Trim();

            Assert.Empty(content);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExportSrt_FormatsTimestampsWithCommaDecimalSeparator()
    {
        var cues = new List<SubtitleCue>
        {
            new() { Start = TimeSpan.FromMilliseconds(1500), End = TimeSpan.FromMilliseconds(2750), SourceText = "Test" }
        };

        var path = Path.GetTempFileName() + ".srt";
        try
        {
            SubtitleFileService.ExportSrt(path, cues);
            var content = File.ReadAllText(path);

            Assert.Contains("00:00:01,500 --> 00:00:02,750", content);
        }
        finally { File.Delete(path); }
    }

    private static string WriteTempSrt(string content)
    {
        var path = Path.GetTempFileName() + ".srt";
        File.WriteAllText(path, content);
        return path;
    }
}
