using BabelPlayer.Core;

namespace BabelPlayer.App;

internal sealed class GeneratedSubtitleCache
{
    private readonly Dictionary<string, List<SubtitleCue>> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void Store(string videoPath, string transcriptionModelKey, IReadOnlyList<SubtitleCue> cues)
    {
        _entries[GetKey(videoPath, transcriptionModelKey)] = SubtitleCueSessionMapper.CloneCues(cues).ToList();
    }

    public bool TryGet(string videoPath, string transcriptionModelKey, out IReadOnlyList<SubtitleCue> cues)
    {
        if (_entries.TryGetValue(GetKey(videoPath, transcriptionModelKey), out var cachedCues))
        {
            cues = SubtitleCueSessionMapper.CloneCues(cachedCues);
            return true;
        }

        cues = [];
        return false;
    }

    private static string GetKey(string videoPath, string transcriptionModelKey)
        => $"{Path.GetFullPath(videoPath)}|{transcriptionModelKey}";
}