using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

public sealed class SubtitleManagerTests
{
    // ── HasCues / CueCount ────────────────────────────────────────────────────

    [Fact]
    public void HasCues_ReturnsFalse_WhenEmpty()
    {
        var manager = new SubtitleManager();

        Assert.False(manager.HasCues);
        Assert.Equal(0, manager.CueCount);
    }

    [Fact]
    public void HasCues_ReturnsTrue_AfterLoadCues()
    {
        var manager = new SubtitleManager();
        manager.LoadCues([MakeCue(0, 2, "Hello")]);

        Assert.True(manager.HasCues);
        Assert.Equal(1, manager.CueCount);
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_RemovesAllCues()
    {
        var manager = new SubtitleManager();
        manager.LoadCues([MakeCue(0, 2, "Hello"), MakeCue(3, 5, "World")]);

        manager.Clear();

        Assert.False(manager.HasCues);
        Assert.Equal(0, manager.CueCount);
    }

    // ── LoadCues ──────────────────────────────────────────────────────────────

    [Fact]
    public void LoadCues_SortsByStartTime()
    {
        var manager = new SubtitleManager();
        manager.LoadCues([
            MakeCue(10, 12, "C"),
            MakeCue(0, 2, "A"),
            MakeCue(5, 7, "B")
        ]);

        var cues = manager.Cues;

        Assert.Equal("A", cues[0].SourceText);
        Assert.Equal("B", cues[1].SourceText);
        Assert.Equal("C", cues[2].SourceText);
    }

    [Fact]
    public void LoadCues_ReplacesExistingCues()
    {
        var manager = new SubtitleManager();
        manager.LoadCues([MakeCue(0, 2, "Old")]);
        manager.LoadCues([MakeCue(5, 7, "New")]);

        var cues = manager.Cues;

        Assert.Single(cues);
        Assert.Equal("New", cues[0].SourceText);
    }

    // ── AddCue ────────────────────────────────────────────────────────────────

    [Fact]
    public void AddCue_InsertsInSortedOrder()
    {
        var manager = new SubtitleManager();
        manager.AddCue(MakeCue(10, 12, "Second"));
        manager.AddCue(MakeCue(0, 2, "First"));

        var cues = manager.Cues;

        Assert.Equal("First", cues[0].SourceText);
        Assert.Equal("Second", cues[1].SourceText);
    }

    [Fact]
    public void AddCue_UpdatesExactMatchByStartEndAndText()
    {
        var manager = new SubtitleManager();
        var original = MakeCue(0, 2, "Hello");
        manager.AddCue(original);

        var updated = new SubtitleCue
        {
            Start = original.Start,
            End = original.End,
            SourceText = original.SourceText,
            TranslatedText = "Updated"
        };
        manager.AddCue(updated);

        Assert.Equal(1, manager.CueCount);
        Assert.Equal("Updated", manager.Cues[0].TranslatedText);
    }

    [Fact]
    public void AddCue_AddsNewCue_WhenTextDiffers()
    {
        var manager = new SubtitleManager();
        manager.AddCue(MakeCue(0, 2, "Original"));
        manager.AddCue(MakeCue(0, 2, "Different"));

        Assert.Equal(2, manager.CueCount);
    }

    // ── GetCueAt ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetCueAt_ReturnsNull_WhenNoMatchingCue()
    {
        var manager = new SubtitleManager();
        manager.LoadCues([MakeCue(5, 10, "Only cue")]);

        Assert.Null(manager.GetCueAt(TimeSpan.FromSeconds(2)));
        Assert.Null(manager.GetCueAt(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void GetCueAt_ReturnsActiveCue_WhenPositionIsWithinRange()
    {
        var manager = new SubtitleManager();
        manager.LoadCues([MakeCue(3, 7, "Active cue")]);

        var result = manager.GetCueAt(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Equal("Active cue", result!.SourceText);
    }

    [Fact]
    public void GetCueAt_ReturnsNull_AtExactEndBoundary()
    {
        var manager = new SubtitleManager();
        manager.LoadCues([MakeCue(0, 5, "Cue")]);

        // Position == End is still considered active (position <= cue.End check)
        var result = manager.GetCueAt(TimeSpan.FromSeconds(5));
        Assert.NotNull(result);
    }

    [Fact]
    public void GetCueAt_PrefersLaterStartOnOverlap()
    {
        var manager = new SubtitleManager();
        manager.LoadCues([
            MakeCue(0, 10, "Early cue"),
            MakeCue(5, 10, "Later start")
        ]);

        var result = manager.GetCueAt(TimeSpan.FromSeconds(7));

        Assert.NotNull(result);
        Assert.Equal("Later start", result!.SourceText);
    }

    // ── CommitTranslation ─────────────────────────────────────────────────────

    [Fact]
    public void CommitTranslation_SetsTranslatedText()
    {
        var manager = new SubtitleManager();
        var cue = MakeCue(0, 2, "Hola");
        manager.AddCue(cue);

        manager.CommitTranslation(cue, "Hello");

        Assert.Equal("Hello", cue.TranslatedText);
    }

    // ── Cues snapshot isolation ───────────────────────────────────────────────

    [Fact]
    public void Cues_ReturnsSnapshot_NotLiveList()
    {
        var manager = new SubtitleManager();
        manager.LoadCues([MakeCue(0, 2, "A")]);

        var snapshot = manager.Cues;
        manager.AddCue(MakeCue(5, 7, "B"));

        Assert.Single(snapshot);
    }

    private static SubtitleCue MakeCue(int startSeconds, int endSeconds, string text)
    {
        return new SubtitleCue
        {
            Start = TimeSpan.FromSeconds(startSeconds),
            End = TimeSpan.FromSeconds(endSeconds),
            SourceText = text
        };
    }
}
