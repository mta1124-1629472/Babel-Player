using System.Globalization;
using System.Text;

namespace PlayerApp.Core;

public class SubtitleManager
{
    private readonly List<SubtitleCue> _cues = [];
    private readonly object _sync = new();

    public void Clear()
    {
        lock (_sync)
        {
            _cues.Clear();
        }
    }

    public bool HasCues
    {
        get
        {
            lock (_sync)
            {
                return _cues.Count > 0;
            }
        }
    }

    public int CueCount
    {
        get
        {
            lock (_sync)
            {
                return _cues.Count;
            }
        }
    }

    public IReadOnlyList<SubtitleCue> Cues
    {
        get
        {
            lock (_sync)
            {
                return _cues.ToList();
            }
        }
    }

    public void LoadSrt(string path)
    {
        LoadCues(SubtitleFileService.ParseSrt(path));
    }

    public void LoadCues(IEnumerable<SubtitleCue> cues)
    {
        lock (_sync)
        {
            _cues.Clear();
            _cues.AddRange(cues.OrderBy(c => c.Start));
        }
    }

    public void AddCue(SubtitleCue cue)
    {
        lock (_sync)
        {
            var existingIndex = _cues.FindIndex(c => c.Start == cue.Start && c.End == cue.End && string.Equals(c.SourceText, cue.SourceText, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                _cues[existingIndex] = cue;
            }
            else
            {
                _cues.Add(cue);
                _cues.Sort((left, right) => left.Start.CompareTo(right.Start));
            }
        }
    }

    public SubtitleCue? GetCueAt(TimeSpan position)
    {
        lock (_sync)
        {
            return _cues.FirstOrDefault(c => position >= c.Start && position <= c.End);
        }
    }

    public void CommitTranslation(SubtitleCue cue, string english)
    {
        cue.TranslatedText = english;
    }

    public void ExportSrt(string path)
    {
        List<SubtitleCue> snapshot;
        lock (_sync)
        {
            snapshot = _cues.ToList();
        }

        var sb = new StringBuilder();
        for (var idx = 0; idx < snapshot.Count; idx++)
        {
            var cue = snapshot[idx];
            sb.AppendLine((idx + 1).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"{FormatTime(cue.Start)} --> {FormatTime(cue.End)}");
            sb.AppendLine(cue.DisplayText);
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    public void ExportTranslatedSrt(string path)
    {
        ExportSrt(path);
    }

    private static string FormatTime(TimeSpan value)
    {
        return value.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
    }
}
