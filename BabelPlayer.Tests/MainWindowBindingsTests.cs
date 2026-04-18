using System;
using System.IO;
using System.Resources;
using System.Text.RegularExpressions;
using Babel.Player.Resources;
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

    [Fact]
    public void MainWindow_AdvertisedPlaybackShortcuts_AppearInBothChromeLayouts()
    {
        var axamlPath = FindRepoFile("Views", "MainWindow.axaml");
        var axaml = File.ReadAllText(axamlPath);

        AssertCommandButtonAttributes(
            axaml,
            "Playback.Preview.PlayPauseSourceCommand",
            expectedTooltipKey: "Tooltip_PlayPause",
            expectedTooltipEnglish: "Play / Pause (Space)",
            expectedAutomationKey: "Automation_PlayPause",
            expectedAutomationEnglish: "Play or pause");
        AssertCommandButtonAttributes(
            axaml,
            "Playback.Preview.ToggleSubtitlesCommand",
            expectedTooltipKey: "Tooltip_ToggleSubtitles",
            expectedTooltipEnglish: "Toggle subtitles (C)",
            expectedAutomationKey: "Automation_ToggleSubtitles",
            expectedAutomationEnglish: "Toggle subtitles");
        AssertCommandButtonAttributes(
            axaml,
            "Playback.Preview.ToggleSegmentPaneCommand",
            expectedTooltipKey: "Tooltip_ToggleSidePanes",
            expectedTooltipEnglish: "Toggle side panels (S)",
            expectedAutomationKey: "Automation_ToggleSidePanes",
            expectedAutomationEnglish: "Toggle side panes");
        AssertCommandButtonAttributes(
            axaml,
            "Playback.Preview.ToggleDubModeCommand",
            expectedTooltipKey: "Tooltip_ToggleDubMode",
            expectedTooltipEnglish: "Toggle Dub Mode (D)",
            expectedAutomationKey: "Automation_ToggleDubMode",
            expectedAutomationEnglish: "Toggle Dub Mode");
        AssertCommandButtonAttributes(
            axaml,
            "Playback.Preview.ToggleFullscreenCommand",
            expectedTooltipKey: "Tooltip_ToggleFullscreen",
            expectedTooltipEnglish: "Toggle fullscreen (F11)",
            expectedAutomationKey: "Automation_ToggleFullscreen",
            expectedAutomationEnglish: "Toggle fullscreen");
    }

    [Fact]
    public void MainWindow_ControlsBar_IsAnchoredToPlayerColumnOnly()
    {
        var axamlPath = FindRepoFile("Views", "MainWindow.axaml");
        var axaml = File.ReadAllText(axamlPath);

        var playerChromeWidthHostTag = FindTagByName(axaml, "PlayerChromeWidthHost");
        Assert.Equal("0", GetAttributeValue(playerChromeWidthHostTag, "Grid.Column"));
        Assert.Equal("4", GetAttributeValue(playerChromeWidthHostTag, "Grid.RowSpan"));

        var controlsBarTag = FindTagByName(axaml, "ControlsBarContainer");
        Assert.Equal("0", GetAttributeValue(controlsBarTag, "Grid.Column"));
        Assert.Null(GetOptionalAttributeValue(controlsBarTag, "Grid.ColumnSpan"));
        Assert.Null(GetOptionalAttributeValue(controlsBarTag, "Margin"));
    }

    [Fact]
    public void MainWindow_CompactChrome_UsesDedicatedPlayerWidthHost()
    {
        var codeBehindPath = FindRepoFile("Views", "MainWindow.axaml.cs");
        var codeBehind = File.ReadAllText(codeBehindPath);

        Assert.Equal(2, CountOccurrences(codeBehind, "FindControl<Control>(\"PlayerChromeWidthHost\")"));
        Assert.Contains("OnPlayerChromeWidthHostSizeChanged", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("FindControl<Control>(\"VideoSegmentsChromeHost\")", codeBehind, StringComparison.Ordinal);
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

    private static void AssertCommandButtonAttributes(
        string axaml,
        string commandBinding,
        string expectedTooltipKey,
        string expectedTooltipEnglish,
        string expectedAutomationKey,
        string expectedAutomationEnglish)
    {
        var tags = FindButtonTagsByCommand(axaml, commandBinding);

        Assert.Equal(2, tags.Length);

        var tooltipMarkup = $"{{local:Localize {expectedTooltipKey}}}";
        var automationMarkup = $"{{local:Localize {expectedAutomationKey}}}";

        // Both chrome layouts must reference the localized keys for the tooltip + automation name.
        Assert.Equal(2, CountOccurrences(axaml, tooltipMarkup));

        foreach (var tag in tags)
        {
            Assert.Equal(tooltipMarkup, GetAttributeValue(tag, "ToolTip.Tip"));
            Assert.Equal(automationMarkup, GetAttributeValue(tag, "AutomationProperties.Name"));
        }

        // The English resx value that ultimately renders through the markup
        // extension must still match the human-visible copy the app ships with.
        Assert.Equal(expectedTooltipEnglish, Strings.ResourceManager.GetString(expectedTooltipKey, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expectedAutomationEnglish, Strings.ResourceManager.GetString(expectedAutomationKey, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string FindTagByName(string axaml, string controlName)
    {
        var match = Regex.Match(
            axaml,
            $@"<\w+\b(?:(?!>).)*\bx:Name=""{Regex.Escape(controlName)}""(?:(?!>).)*>",
            RegexOptions.Singleline);

        Assert.True(match.Success, $"Expected tag with x:Name='{controlName}'.");
        return match.Value;
    }

    private static string[] FindButtonTagsByCommand(string axaml, string commandBinding)
    {
        var pattern = $@"<Button\b(?:(?!>).)*Command=""\{{Binding {Regex.Escape(commandBinding)}\}}""(?:(?!>).)*>";
        var matches = Regex.Matches(axaml, pattern, RegexOptions.Singleline);
        var results = new string[matches.Count];
        for (var i = 0; i < matches.Count; i++)
            results[i] = matches[i].Value;

        return results;
    }

    private static string GetAttributeValue(string tag, string attributeName)
    {
        var match = Regex.Match(
            tag,
            $@"\b{Regex.Escape(attributeName)}=""([^""]+)""",
            RegexOptions.Singleline);

        Assert.True(match.Success, $"Expected attribute '{attributeName}' in tag: {tag}");
        return match.Groups[1].Value;
    }

    private static string? GetOptionalAttributeValue(string tag, string attributeName)
    {
        var match = Regex.Match(
            tag,
            $@"\b{Regex.Escape(attributeName)}=""([^""]+)""",
            RegexOptions.Singleline);

        return match.Success ? match.Groups[1].Value : null;
    }
}
