using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class TranslationArtifactStreamingWriterTests : IDisposable
{
    private readonly string _dir;

    public TranslationArtifactStreamingWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-translation-streaming-writer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public async Task ApplyTranslatedTextAsync_UpdatesSegmentByIdAndPersistsArtifact()
    {
        var partialPath = Path.Combine(_dir, "translation.partial.json");
        var writer = new TranslationArtifactStreamingWriter(partialPath, "es", "en");
        var ct = CancellationToken.None;
        await writer.InitializeAsync(ct);

        var first = new TranscriptChannelItem(
            "segment_0.0",
            new TranscriptSegmentArtifact { Start = 0.0, End = 1.0, Text = "Hola" },
            "es",
            0.99);
        var second = new TranscriptChannelItem(
            "segment_1.0",
            new TranscriptSegmentArtifact { Start = 1.0, End = 2.0, Text = "Mundo" },
            "es",
            0.99);

        await writer.AppendPendingSegmentAsync(first, ct);
        await writer.AppendPendingSegmentAsync(second, ct);

        var updated = await writer.ApplyTranslatedTextAsync("segment_1.0", "World", "es", "en", ct);
        Assert.Equal("segment_1.0", updated.Id);
        Assert.Equal("World", updated.TranslatedText);

        var finalPath = Path.Combine(_dir, "translation.final.json");
        await writer.CompleteAsync(finalPath, ct);
        var artifact = await ArtifactJson.LoadTranslationAsync(finalPath, ct);
        var seg0 = artifact.Segments!.Single(s => s.Id == "segment_0.0");
        var seg1 = artifact.Segments!.Single(s => s.Id == "segment_1.0");
        Assert.True(string.IsNullOrEmpty(seg0.TranslatedText));
        Assert.Equal("World", seg1.TranslatedText);
    }

    [Fact]
    public async Task ApplyTranslatedTextAsync_ThrowsWhenSegmentIdDoesNotExist()
    {
        var partialPath = Path.Combine(_dir, "missing.partial.json");
        var writer = new TranslationArtifactStreamingWriter(partialPath, "es", "en");
        await writer.InitializeAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.ApplyTranslatedTextAsync("segment_missing", "ignored", "es", "en", CancellationToken.None));
        Assert.Contains("segment_missing", ex.Message, StringComparison.Ordinal);
    }
}
