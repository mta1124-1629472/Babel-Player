using System;
using System.IO;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class MainWindowBindingsTests
{
    [Fact]
    public void MainWindow_DubMixSlider_BindsToModeAwarePreviewProperties()
    {
        var axamlPath = FindRepoFile("Views", "MainWindow.axaml");
        var axaml = File.ReadAllText(axamlPath);

        Assert.Equal(2, CountOccurrences(axaml, "Playback.Preview.DubMixControlDb"));
        Assert.Equal(2, CountOccurrences(axaml, "Playback.Preview.DubMixControlLabel"));
        Assert.Equal(2, CountOccurrences(axaml, "Playback.Preview.DubMixControlTooltip"));
        Assert.Equal(2, CountOccurrences(axaml, "Playback.Preview.DubMixControlValueLabel"));
    }

    private static string FindRepoFile(params string[] relativePathParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. relativePathParts]);
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repo file '{Path.Combine(relativePathParts)}' from '{AppContext.BaseDirectory}'.");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
