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

        if (string.IsNullOrWhiteSpace(session.SourceMediaPath) || !File.Exists(session.SourceMediaPath))
            issues.Add("Source media is missing.");

        if (string.IsNullOrWhiteSpace(options.OutputPath))
            issues.Add("Output path is required.");

        if (options.IncludeTtsAudio && (string.IsNullOrWhiteSpace(session.TtsPath) || !File.Exists(session.TtsPath)))
            issues.Add("No combined TTS audio is available for export.");

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

        var inputFiles = new List<string> { session.SourceMediaPath! };
        var args = new List<string>();

        if (options.OverwriteExisting)
            args.Add("-y");
        else
            args.Add("-n");

        args.Add("-i");
        args.Add(session.SourceMediaPath!);

        if (options.IncludeTtsAudio)
        {
            inputFiles.Add(session.TtsPath!);
            args.Add("-i");
            args.Add(session.TtsPath!);
        }

        var subtitleFilePath = (options.IncludeSoftCaptions || options.BurnInCaptions)
            ? WriteSubtitleFile(session, segments)
            : null;

        if (subtitleFilePath is not null)
        {
            inputFiles.Add(subtitleFilePath);
        }

        if (options.IncludeTtsAudio)
        {
            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-map");
            args.Add("1:a:0");

            if (subtitleFilePath is not null && options.IncludeSoftCaptions)
            {
                args.Add("-map");
                args.Add("2");
                args.Add("-c:s");
                args.Add("mov_text");
            }

            args.Add("-c:v");
            args.Add(options.Encoder ?? "libx264");
            args.Add("-c:a");
            args.Add("aac");
        }
        else
        {
            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-map");
            args.Add("0:a?");

            if (subtitleFilePath is not null && options.IncludeSoftCaptions)
            {
                args.Add("-map");
                args.Add("1");
                args.Add("-c:s");
                args.Add("mov_text");
            }

            args.Add("-c:v");
            args.Add(options.Encoder ?? "libx264");
        }

        if (options.BurnInCaptions && subtitleFilePath is not null)
        {
            var escaped = EscapeForFfmpegFilter(subtitleFilePath);
            args.Add("-vf");
            args.Add($"subtitles={escaped}");
        }

        args.Add(options.OutputPath);

        return new ExportVideoPlan(
            session.SourceMediaPath!,
            options.OutputPath,
            options.IncludeTtsAudio,
            options.IncludeSoftCaptions,
            options.BurnInCaptions,
            inputFiles,
            args,
            subtitleFilePath);
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
