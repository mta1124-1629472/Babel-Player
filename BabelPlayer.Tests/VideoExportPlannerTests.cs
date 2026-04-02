using System;
using System.Collections.Generic;
using System.IO;
using Babel.Player.Models;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class VideoExportPlannerTests
{
    private static WorkflowSessionSnapshot CreateSession(string workDir)
    {
        var source = Path.Combine(workDir, "source.mp4");
        var tts = Path.Combine(workDir, "tts.mp3");
        File.WriteAllText(source, "source");
        File.WriteAllText(tts, "tts");

        return WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            SourceMediaPath = source,
            TranscriptPath = Path.Combine(workDir, "transcript.json"),
            TranslationPath = Path.Combine(workDir, "translation.json"),
            TtsPath = tts,
        };
    }

    private static IReadOnlyList<WorkflowSegmentState> CreateSegments() =>
        [
            new WorkflowSegmentState("segment_0.0", 0, 2, "Hello", true, "Hola", true),
            new WorkflowSegmentState("segment_2.0", 2, 4, "World", true, "Mundo", true),
        ];

    [Fact]
    public void Validate_WhenRequiredArtifactsExist_AllowsExport()
    {
        var workDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var planner = new VideoExportPlanner();
            var session = CreateSession(workDir);
            var result = planner.Validate(session, CreateSegments(), new ExportVideoOptions(Path.Combine(workDir, "out.mp4")));

            Assert.True(result.CanExport);
            Assert.Empty(result.Issues);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void BuildPlan_WithSoftCaptions_AddsSubtitleInputAndMovTextMapping()
    {
        var workDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var planner = new VideoExportPlanner();
            var session = CreateSession(workDir);
            var plan = planner.BuildPlan(
                session,
                CreateSegments(),
                new ExportVideoOptions(Path.Combine(workDir, "out.mp4"), IncludeSoftCaptions: true));

            Assert.Contains("-c:s", plan.FfmpegArguments);
            Assert.Contains("mov_text", plan.FfmpegArguments);
            Assert.NotNull(plan.SubtitleFilePath);
            Assert.True(File.Exists(plan.SubtitleFilePath));
            Assert.Contains(plan.SubtitleFilePath!, plan.InputFiles);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void BuildPlan_WithBurnInCaptions_AddsSubtitleFilter()
    {
        var workDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var planner = new VideoExportPlanner();
            var session = CreateSession(workDir);
            var plan = planner.BuildPlan(
                session,
                CreateSegments(),
                new ExportVideoOptions(Path.Combine(workDir, "out.mp4"), IncludeSoftCaptions: false, BurnInCaptions: true));

            Assert.Contains("-vf", plan.FfmpegArguments);
            Assert.Contains(plan.FfmpegArguments, arg => arg.StartsWith("subtitles=", StringComparison.Ordinal));
            Assert.NotNull(plan.SubtitleFilePath);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
