using System;
using System.IO;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class ExecutionSnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-execution-snapshot-tests-{Guid.NewGuid():N}");

    public ExecutionSnapshotTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void CaptureTranslationTranscriptIdentity_ThrowsWhenTranscriptIsMissingAndPendingIsNotAllowed()
    {
        var transcriptPath = Path.Combine(_dir, "missing.json");

        Assert.Throws<FileNotFoundException>(() =>
            SessionWorkflowCoordinator.CaptureTranslationTranscriptIdentity(
                transcriptPath,
                allowPendingTranscriptArtifact: false));
    }

    [Fact]
    public void CaptureTranslationTranscriptIdentity_AllowsPendingTranscriptArtifactForStreamingPipeline()
    {
        var transcriptPath = Path.Combine(_dir, "clip.json");

        var identity = SessionWorkflowCoordinator.CaptureTranslationTranscriptIdentity(
            transcriptPath,
            allowPendingTranscriptArtifact: true);

        Assert.True(identity.IsPending);

        File.WriteAllText(transcriptPath, "{}");

        Assert.True(identity.Matches(transcriptPath));
    }

    [Fact]
    public void CaptureTranslationTranscriptIdentity_PendingArtifactStillRequiresSameResolvedPath()
    {
        var transcriptPath = Path.Combine(_dir, "clip.json");
        var otherTranscriptPath = Path.Combine(_dir, "other.json");

        var identity = SessionWorkflowCoordinator.CaptureTranslationTranscriptIdentity(
            transcriptPath,
            allowPendingTranscriptArtifact: true);

        File.WriteAllText(otherTranscriptPath, "{}");

        Assert.False(identity.Matches(otherTranscriptPath));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
