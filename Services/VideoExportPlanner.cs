using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed class VideoExportPlanner
{
    public ExportVideoValidationResult Validate(
        WorkflowSessionSnapshot session,
        IReadOnlyList<WorkflowSegmentState> segments,
        ExportVideoOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(options);

        var issues = new List<string>();

        var videoIn = ResolveVideoInputPath(session);
        if (string.IsNullOrWhiteSpace(videoIn) || !File.Exists(videoIn))
            issues.Add("Source media is missing.");

        if (string.IsNullOrWhiteSpace(options.OutputPath))
            issues.Add("Output path is required.");

        var preferredDubPath = ResolvePreferredDubPath(session, options);
        if (options.IncludeTtsAudio && (string.IsNullOrWhiteSpace(preferredDubPath) || !File.Exists(preferredDubPath)))
            issues.Add("No dubbed audio is available for export.");

        if ((options.IncludeSoftCaptions || options.BurnInCaptions) && segments.Count == 0)
            issues.Add("No segment data is available for captions.");

        if (options.BurnInCaptions && string.IsNullOrWhiteSpace(session.TranscriptPath) && string.IsNullOrWhiteSpace(session.TranslationPath))
            issues.Add("Burn-in captions requires transcript or translation text.");

        return new ExportVideoValidationResult(issues.Count == 0, issues);
    }

    public ExportVideoPlan BuildPlan(
        WorkflowSessionSnapshot session,
        IReadOnlyList<WorkflowSegmentState> segments,
        ExportVideoOptions options)
    {
        var validation = Validate(session, segments, options);
        if (!validation.CanExport)
            throw new InvalidOperationException(string.Join(" ", validation.Issues));
        var preferredDubPath = ResolvePreferredDubPath(session, options);
        var videoIn = ResolveVideoInputPath(session)!;

        var inputFiles = new List<string> { videoIn };
        var args = new List<string>();

        if (options.OverwriteExisting)
            args.Add("-y");
        else
            args.Add("-n");

        args.Add("-i");
        args.Add(videoIn);

        if (options.IncludeTtsAudio)
        {
            inputFiles.Add(preferredDubPath!);
            args.Add("-i");
            args.Add(preferredDubPath!);
        }

        string? subtitleFilePath = null;
        if (options.IncludeSoftCaptions || options.BurnInCaptions)
            subtitleFilePath = WriteSubtitleFile(session, segments);

        // Burn-in and soft-mux use the same SRT; avoid muxing a second text track when burning in.
        var muxSoftSubs = options.IncludeSoftCaptions && !options.BurnInCaptions && subtitleFilePath is not null;

        // Soft-muxed subtitles need a dedicated input; burn-in reads the file via -vf only.
        if (muxSoftSubs)
        {
            inputFiles.Add(subtitleFilePath!);
            args.Add("-i");
            args.Add(subtitleFilePath!);
        }

        var encoder = options.Encoder ?? "libx264";

        // Input indices: 0 = video, 1 = dub (optional), last = soft subs (optional)
        if (options.BurnInCaptions && subtitleFilePath is not null)
        {
            var escaped = EscapeForFfmpegFilter(subtitleFilePath);
            args.Add("-vf");
            args.Add($"subtitles={escaped}");
        }

        const int VideoIn = 0;
        var softSubIn = muxSoftSubs
            ? (options.IncludeTtsAudio ? 2 : 1)
            : (int?)null;

        args.Add("-map");
        args.Add($"{VideoIn}:v:0");

        if (options.IncludeTtsAudio)
        {
            args.Add("-map");
            args.Add("1:a:0");
        }
        else
        {
            args.Add("-map");
            args.Add($"{VideoIn}:a?");
        }

        if (softSubIn is not null)
        {
            args.Add("-map");
            args.Add($"{softSubIn}:s:0");
            args.Add("-c:s");
            args.Add("mov_text");
        }

        args.Add("-c:v");
        args.Add(encoder);
        HardwareEncoderHelper.AppendRecommendedVideoQualityArgs(args, encoder);

        if (options.IncludeTtsAudio)
        {
            args.Add("-c:a");
            args.Add("aac");
        }

        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(options.OutputPath);

        return new ExportVideoPlan(
            videoIn,
            options.OutputPath,
            options.IncludeTtsAudio,
            options.IncludeSoftCaptions,
            options.BurnInCaptions,
            inputFiles,
            args,
            subtitleFilePath);
    }

    /// <summary>Prefer ingested media (what the player loads) over the original source path.</summary>
    public static string? ResolveVideoInputPath(WorkflowSessionSnapshot session)
    {
        if (!string.IsNullOrWhiteSpace(session.IngestedMediaPath) && File.Exists(session.IngestedMediaPath))
            return session.IngestedMediaPath;
        if (!string.IsNullOrWhiteSpace(session.SourceMediaPath) && File.Exists(session.SourceMediaPath))
            return session.SourceMediaPath;
        return null;
    }

    private static string? ResolvePreferredDubPath(WorkflowSessionSnapshot session, ExportVideoOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DubAudioPathOverride) && File.Exists(options.DubAudioPathOverride))
            return options.DubAudioPathOverride;
        if (!string.IsNullOrWhiteSpace(session.MixedDubAudioPath) && File.Exists(session.MixedDubAudioPath))
            return session.MixedDubAudioPath;
        return session.TtsPath;
    }

    public string BuildSubtitleText(IReadOnlyList<WorkflowSegmentState> segments) =>
        SrtGenerator.Generate(segments);

    public string WriteSubtitleFile(
        WorkflowSessionSnapshot session,
        IReadOnlyList<WorkflowSegmentState> segments)
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer",
            "exports",
            session.SessionId.ToString("N"));

        Directory.CreateDirectory(baseDir);
        var subtitlePath = Path.Combine(baseDir, "captions.srt");
        File.WriteAllText(subtitlePath, BuildSubtitleText(segments));
        return subtitlePath;
    }

    public static string EscapeForFfmpegFilter(string path)
    {
        return path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
    }
}
