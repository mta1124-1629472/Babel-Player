using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

public sealed class PlaybackSessionControllerTests
{
    private static PlaybackSessionController CreateWithItems(params string[] paths)
    {
        var playlist = new PlaylistController();
        playlist.EnqueueFiles(paths);
        return new PlaybackSessionController(playlist);
    }

    [Fact]
    public void PlaybackSessionController_StartWith_SetsCurrentItem()
    {
        var controller = CreateWithItems("a.mp4", "b.mp4", "c.mp4");
        var target = new PlaylistItem { Path = "b.mp4", DisplayName = "b.mp4" };

        var result = controller.StartWith(target);

        Assert.NotNull(result);
        Assert.Equal("b.mp4", result!.Path);
        Assert.Equal("b.mp4", controller.CurrentItem?.Path);
    }

    [Fact]
    public void PlaybackSessionController_MoveNext_AdvancesToNextItem()
    {
        var controller = CreateWithItems("a.mp4", "b.mp4", "c.mp4");
        controller.StartWith(new PlaylistItem { Path = "a.mp4", DisplayName = "a.mp4" });

        var next = controller.MoveNext();

        Assert.NotNull(next);
        Assert.Equal("b.mp4", next!.Path);
    }

    [Fact]
    public void PlaybackSessionController_MovePrevious_GoesToPreviousItem()
    {
        var controller = CreateWithItems("a.mp4", "b.mp4", "c.mp4");
        controller.StartWith(new PlaylistItem { Path = "b.mp4", DisplayName = "b.mp4" });

        var prev = controller.MovePrevious();

        Assert.NotNull(prev);
        Assert.Equal("a.mp4", prev!.Path);
    }

    [Fact]
    public void PlaybackSessionController_MoveNext_ReturnsNullAtEnd()
    {
        var controller = CreateWithItems("a.mp4", "b.mp4");
        controller.StartWith(new PlaylistItem { Path = "b.mp4", DisplayName = "b.mp4" });

        var next = controller.MoveNext();

        Assert.Null(next);
    }

    [Fact]
    public void PlaybackSessionController_MovePrevious_ReturnsNullAtStart()
    {
        var controller = CreateWithItems("a.mp4", "b.mp4");
        controller.StartWith(new PlaylistItem { Path = "a.mp4", DisplayName = "a.mp4" });

        var prev = controller.MovePrevious();

        Assert.Null(prev);
    }

    [Fact]
    public void PlaybackSessionController_BuildResumeEntry_CreatesEntryFromSnapshot()
    {
        var controller = CreateWithItems("video.mp4");
        var snapshot = new PlaybackStateSnapshot
        {
            Path = "video.mp4",
            Position = TimeSpan.FromSeconds(120),
            Duration = TimeSpan.FromMinutes(10)
        };

        var entry = controller.BuildResumeEntry(snapshot);

        Assert.NotNull(entry);
        Assert.Equal("video.mp4", entry!.Path);
        Assert.Equal(120.0, entry.PositionSeconds);
        Assert.Equal(600.0, entry.DurationSeconds);
    }

    [Fact]
    public void PlaybackSessionController_BuildResumeEntry_ReturnsNullForZeroPosition()
    {
        var controller = CreateWithItems("video.mp4");
        var snapshot = new PlaybackStateSnapshot
        {
            Path = "video.mp4",
            Position = TimeSpan.Zero,
            Duration = TimeSpan.FromMinutes(10)
        };

        Assert.Null(controller.BuildResumeEntry(snapshot));
    }

    [Fact]
    public void PlaybackSessionController_BuildResumeEntry_ReturnsNullForZeroDuration()
    {
        var controller = CreateWithItems("video.mp4");
        var snapshot = new PlaybackStateSnapshot
        {
            Path = "video.mp4",
            Position = TimeSpan.FromSeconds(30),
            Duration = TimeSpan.Zero
        };

        Assert.Null(controller.BuildResumeEntry(snapshot));
    }
}
