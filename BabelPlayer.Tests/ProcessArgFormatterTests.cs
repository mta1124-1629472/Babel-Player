using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class ProcessArgFormatterTests
{
    [Fact]
    public void FormatArgs_LeavesSimpleTokensUnquoted()
    {
        var formatted = ProcessArgFormatter.FormatArgs(["uv", "pip", "install", "--upgrade"]);
        Assert.Equal("uv pip install --upgrade", formatted);
    }

    [Fact]
    public void FormatArgs_QuotesWhitespaceAndEscapesEmbeddedQuotes()
    {
        var formatted = ProcessArgFormatter.FormatArgs([
            "arg with spaces",
            "embedded\"quote",
            "line1\nline2",
            "tab\tvalue",
            "slash\\tail",
        ]);

        Assert.Equal(
            "\"arg with spaces\" \"embedded\\\"quote\" \"line1\\nline2\" \"tab\\tvalue\" slash\\tail",
            formatted);
    }
}
