using System;
using System.Collections.Generic;
using System.Text;
using Babel.Player.Models;

namespace Babel.Player.Services;

/// <summary>
/// Generates SRT subtitle file content from a list of workflow segments.
/// </summary>
public static class SrtGenerator
{
    /// <summary>
    /// Converts a segment list to SRT format text.
    /// Uses TranslatedText when available, falls back to SourceText.
    /// Segments with no displayable text are skipped.
    /// </summary>
    public static string Generate(IEnumerable<WorkflowSegmentState> segments)
    {
        var sb = new StringBuilder();
        int index = 1;
        foreach (var seg in segments)
        {
            var text = seg.TranslatedText ?? seg.SourceText;
            if (string.IsNullOrWhiteSpace(text)) continue;

            sb.AppendLine(index++.ToString());
            sb.AppendLine($"{Stamp(seg.StartSeconds)} --> {Stamp(seg.EndSeconds)}");
            sb.AppendLine(text.Trim());
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // SRT timestamp format: HH:MM:SS,mmm
    private static string Stamp(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }
}
