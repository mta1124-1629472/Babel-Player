using BabelPlayer.App;

namespace BabelPlayer.App.Tests;

public sealed class LibraryBrowserServiceTests
{
    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    [InlineData(".avi")]
    [InlineData(".mov")]
    [InlineData(".webm")]
    public void LibraryBrowserService_IsSupportedMediaFile_ReturnsTrueForVideoExtensions(string ext)
    {
        Assert.True(LibraryBrowserService.IsSupportedMediaFile($"video{ext}"));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".doc")]
    [InlineData(".cs")]
    [InlineData(".exe")]
    public void LibraryBrowserService_IsSupportedMediaFile_ReturnsFalseForNonMediaExtensions(string ext)
    {
        Assert.False(LibraryBrowserService.IsSupportedMediaFile($"file{ext}"));
    }

    [Theory]
    [InlineData(".MP4")]
    [InlineData(".Mkv")]
    [InlineData(".AVI")]
    public void LibraryBrowserService_IsSupportedMediaFile_IsCaseInsensitive(string ext)
    {
        Assert.True(LibraryBrowserService.IsSupportedMediaFile($"video{ext}"));
    }

    [Fact]
    public void LibraryBrowserService_EnumerateMediaFiles_FindsVideoFilesInDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"babel_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "clip1.mp4"), "");
            File.WriteAllText(Path.Combine(tempDir, "clip2.mp4"), "");
            File.WriteAllText(Path.Combine(tempDir, "readme.txt"), "");

            var service = new LibraryBrowserService();
            var files = service.EnumerateMediaFiles(tempDir, recursive: false);

            Assert.Equal(2, files.Count);
            Assert.All(files, f => Assert.EndsWith(".mp4", f));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LibraryBrowserService_BuildRootNode_CreatesNodeFromExistingDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"babel_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "movie.mkv"), "");

            var service = new LibraryBrowserService();
            var node = service.BuildRootNode(tempDir);

            Assert.NotNull(node);
            Assert.Equal(Path.GetFileName(tempDir), node.Name);
            Assert.True(node.IsFolder);
            Assert.Single(node.Children);
            Assert.Equal("movie.mkv", node.Children[0].Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LibraryBrowserService_BuildPinnedRoots_BuildsNodesFromValidPaths()
    {
        var tempDir1 = Path.Combine(Path.GetTempPath(), $"babel_test_{Guid.NewGuid():N}");
        var tempDir2 = Path.Combine(Path.GetTempPath(), $"babel_test_{Guid.NewGuid():N}");
        var nonExistent = Path.Combine(Path.GetTempPath(), $"babel_test_{Guid.NewGuid():N}_missing");
        Directory.CreateDirectory(tempDir1);
        Directory.CreateDirectory(tempDir2);
        try
        {
            var service = new LibraryBrowserService();
            var roots = service.BuildPinnedRoots([tempDir1, nonExistent, tempDir2]);

            Assert.Equal(2, roots.Count);
            Assert.Equal(Path.GetFileName(tempDir1), roots[0].Name);
            Assert.Equal(Path.GetFileName(tempDir2), roots[1].Name);
        }
        finally
        {
            Directory.Delete(tempDir1, recursive: true);
            Directory.Delete(tempDir2, recursive: true);
        }
    }
}
