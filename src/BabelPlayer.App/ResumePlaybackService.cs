using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class ResumePlaybackService
{
    private static readonly TimeSpan MinimumResumePosition = TimeSpan.FromSeconds(60);
    private const double MinimumResumeProgressRatio = 0.05;
    private const double FinalWindowRatio = 0.03;
    private static readonly TimeSpan MinimumResumeDurationFallback = TimeSpan.FromMinutes(10);

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
            || snapshot.Duration <= TimeSpan.Zero
            || snapshot.Position <= TimeSpan.Zero)
        {
            return null;
        }

        if (!IsMeaningfulResumePosition(snapshot.Position, snapshot.Duration))
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

        var position = TimeSpan.FromSeconds(Math.Clamp(entry.PositionSeconds, 0, duration.TotalSeconds));
        return IsMeaningfulResumePosition(position, duration)
            ? Clone(entry)
            : null;
    }

    public void Update(PlaybackStateSnapshot snapshot, bool forceRemoveCompleted = false)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Path) || snapshot.Duration <= TimeSpan.Zero)
        {
            return;
        }

        _entries.RemoveAll(entry => string.Equals(entry.Path, snapshot.Path, StringComparison.OrdinalIgnoreCase));
        if (forceRemoveCompleted || !IsMeaningfulResumePosition(snapshot.Position, snapshot.Duration))
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

    private static bool IsMeaningfulResumePosition(TimeSpan position, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return false;
        }

        var normalizedPosition = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds));
        if (normalizedPosition < MinimumResumePosition)
        {
            return false;
        }

        var minimumDuration = MinimumResumeDurationFallback;
        if (duration < minimumDuration)
        {
            return false;
        }

        var minimumByPercent = TimeSpan.FromSeconds(duration.TotalSeconds * MinimumResumeProgressRatio);
        if (normalizedPosition < minimumByPercent)
        {
            return false;
        }

        var finalWindowStart = TimeSpan.FromSeconds(duration.TotalSeconds * (1 - FinalWindowRatio));
        if (normalizedPosition >= finalWindowStart)
        {
            return false;
        }

        return true;
    }
}
