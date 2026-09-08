using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class StreamingTranslationCommitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-streaming-commit-{Guid.NewGuid():N}");

    public StreamingTranslationCommitTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public async Task PersistStreamingWorkingArtifact_WritesValidFileThatFinalizes()
    {
        var workingPath = Path.Combine(_dir, "clip_fr.json.work");
        var segments = new List<TranslationSegmentArtifact>
        {
            new() { Id = "segment_0.0", Start = 0.0, End = 1.5, Text = "hola", TranslatedText = "hello", SpeakerId = "S0" },
            new() { Id = "segment_1.5", Start = 1.5, End = 3.0, Text = "mundo", TranslatedText = "world", SpeakerId = "S0" },
        };

        await SessionWorkflowCoordinator.StreamingPipelineOrchestrator.PersistStreamingTranslationWorkingArtifactAsync(
            workingPath, "es", "fr", segments, CancellationToken.None);

        Assert.True(File.Exists(workingPath));
        var reloaded = await ArtifactJson.LoadTranslationAsync(workingPath, CancellationToken.None);
        Assert.Equal(2, reloaded.Segments!.Count);
        Assert.Equal("segment_0.0", reloaded.Segments[0].Id);
        Assert.Equal("S0", reloaded.Segments[0].SpeakerId);
        Assert.Equal("hello", reloaded.Segments[0].TranslatedText);

        var finalPath = await ArtifactIntegrity.FinalizeWorkingArtifactAsync(workingPath, CancellationToken.None);
        Assert.Equal(Path.Combine(_dir, "clip_fr.json"), finalPath);
        Assert.True(File.Exists(finalPath));
        Assert.False(File.Exists(workingPath));
    }
}
