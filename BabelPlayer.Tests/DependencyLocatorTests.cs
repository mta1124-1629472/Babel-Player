using System.IO;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class DependencyLocatorTests
{
    [Fact]
    public void GetPiperCandidatePaths_IncludesBundledToolsRidDirectory()
    {
        var appDir = Path.Combine(Path.GetTempPath(), "babel-piper-candidates");
        var candidates = DependencyLocator.GetPiperCandidatePaths(appDir, "win-x64");

        Assert.Contains(
            Path.Combine(appDir, "tools", "win-x64", "piper", "piper.exe"),
            candidates);
    }
}
