using System.Globalization;

namespace BabelPlayer.Core;

public static class SubtitleFileService
{
    public static IReadOnlyList<SubtitleCue> ParseSrt(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<SubtitleCue>();
        }

        var content = File.ReadAllText(path);
        var blocks = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        var cues = new List<SubtitleCue>();

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2)
            {
                continue;
            }

            var timelineIndex = lines[0].Contains("-->", StringComparison.Ordinal) ? 0 : 1;
            if (timelineIndex >= lines.Length || !lines[timelineIndex].Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = lines[timelineIndex].Split("-->", StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !TryParseTime(parts[0], out var start) || !TryParseTime(parts[1], out var end))
            {
                continue;
            }

            var text = string.Join(Environment.NewLine, lines.Skip(timelineIndex + 1));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            cues.Add(new SubtitleCue
            {
                Start = start,
                End = end,
                SourceText = text
            });
        }

        return cues;
    }

    public static void ExportSrt(string path, IReadOnlyList<SubtitleCue> cues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(cues);

        var builder = new System.Text.StringBuilder();
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            if (string.IsNullOrWhiteSpace(cue.SourceText) && string.IsNullOrWhiteSpace(cue.TranslatedText))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine((index + 1).ToString(CultureInfo.InvariantCulture));
            builder.Append(FormatTime(cue.Start));
            builder.Append(" --> ");
            builder.AppendLine(FormatTime(cue.End));
            builder.AppendLine((cue.DisplayText ?? cue.SourceText).Trim());
        }

        File.WriteAllText(path, builder.ToString());
    }

    private static bool TryParseTime(string input, out TimeSpan result)
    {
        var normalized = input.Replace(',', '.');
        return TimeSpan.TryParseExact(
            normalized,
            ["hh\\:mm\\:ss\\.fff", "h\\:mm\\:ss\\.fff"],
            CultureInfo.InvariantCulture,
            out result);
    }

    private static string FormatTime(TimeSpan value)
    {
        return value.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
    }
}
