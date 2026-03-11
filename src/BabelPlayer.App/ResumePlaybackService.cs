using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class ResumePlaybackService
{
    private readonly SettingsFacade _settingsFacade;
    private readonly Action<IReadOnlyList<PlaybackResumeEntry>>? _persistEntries;
    private readonly List<PlaybackResumeEntry> _entries;

    public ResumePlaybackService(
        SettingsFacade? settingsFacade = null,
        IEnumerable<PlaybackResumeEntry>? initialEntries = null,
        Action<IReadOnlyList<PlaybackResumeEntry>>? persistEntries = null)
    {
        _settingsFacade = settingsFacade ?? new SettingsFacade();
        _persistEntries = persistEntries;
        _entries = (initialEntries ?? _settingsFacade.LoadResumeEntries()).Select(Clone).ToList();
    }

    public IReadOnlyList<PlaybackResumeEntry> Entries => _entries.Select(Clone).ToArray();

    public PlaybackResumeEntry? BuildEntry(PlaybackStateSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Path)
            || snapshot.Duration <= TimeSpan.FromMinutes(2)
            || snapshot.Position <= TimeSpan.Zero)
        {
            return null;
        }

        return new PlaybackResumeEntry
        {
            Path = snapshot.Path,
            PositionSeconds = snapshot.Position.TotalSeconds,
            DurationSeconds = snapshot.Duration.TotalSeconds,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public PlaybackResumeEntry? FindEntry(string? path, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(path) || duration <= TimeSpan.Zero)
        {
            return null;
        }

        var entry = _entries
            .Where(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (entry is null)
        {
            return null;
        }

        return entry.PositionSeconds < TimeSpan.FromMinutes(2).TotalSeconds
            || entry.PositionSeconds >= duration.TotalSeconds * 0.95
            ? null
            : Clone(entry);
    }

    public void Update(PlaybackStateSnapshot snapshot, bool forceRemoveCompleted = false)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Path) || snapshot.Duration <= TimeSpan.FromMinutes(2))
        {
            return;
        }

        _entries.RemoveAll(entry => string.Equals(entry.Path, snapshot.Path, StringComparison.OrdinalIgnoreCase));
        var completionRatio = snapshot.Duration.TotalSeconds <= 0
            ? 0
            : snapshot.Position.TotalSeconds / snapshot.Duration.TotalSeconds;
        if (forceRemoveCompleted || completionRatio >= 0.95 || snapshot.Position < TimeSpan.FromMinutes(2))
        {
            Persist();
            return;
        }

        var entry = BuildEntry(snapshot);
        if (entry is null)
        {
            Persist();
            return;
        }

        _entries.Add(entry);
        Persist();
    }

    public void RemoveCompletedEntry(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _entries.RemoveAll(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
        Persist();
    }

    public void Clear()
    {
        _entries.Clear();
        Persist();
    }

    private void Persist()
    {
        var snapshot = _entries.Select(Clone).ToArray();
        if (_persistEntries is not null)
        {
            _persistEntries(snapshot);
            return;
        }

        _settingsFacade.SaveResumeEntries(snapshot);
    }

    private static PlaybackResumeEntry Clone(PlaybackResumeEntry entry)
    {
        return new PlaybackResumeEntry
        {
            Path = entry.Path,
            PositionSeconds = entry.PositionSeconds,
            DurationSeconds = entry.DurationSeconds,
            UpdatedAt = entry.UpdatedAt
        };
    }
}
