using BabelPlayer.Core;

namespace BabelPlayer.App;

public interface IShellPreferencesService
{
    event Action<ShellPreferencesSnapshot>? SnapshotChanged;

    ShellPreferencesSnapshot Current { get; }

    ShellPreferencesSnapshot ApplyLayoutChange(ShellLayoutPreferencesChange change);

    ShellPreferencesSnapshot ApplyPlaybackDefaultsChange(ShellPlaybackDefaultsChange change);

    ShellPreferencesSnapshot ApplySubtitlePresentationChange(ShellSubtitlePresentationChange change);

    ShellPreferencesSnapshot ApplyAudioStateChange(ShellAudioStateChange change);

    ShellPreferencesSnapshot ApplyShortcutProfileChange(ShellShortcutProfileChange change);

    ShellPreferencesSnapshot ApplyResumeEnabledChange(ShellResumeEnabledChange change);

    ShellPreferencesSnapshot ApplyPinnedRootsChange(ShellPinnedRootsChange change);
}

public sealed record ShellPreferencesSnapshot
{
    public HardwareDecodingMode HardwareDecodingMode { get; init; } = HardwareDecodingMode.AutoSafe;
    public SubtitleRenderMode SubtitleRenderMode { get; init; } = SubtitleRenderMode.TranslationOnly;
    public SubtitleStyleSettings SubtitleStyle { get; init; } = new();
    public ShortcutProfile ShortcutProfile { get; init; } = ShortcutProfile.CreateDefault();
    public IReadOnlyList<string> PinnedRoots { get; init; } = [];
    public double VolumeLevel { get; init; } = 0.8;
    public bool IsMuted { get; init; }
    public double PlaybackRate { get; init; } = 1.0;
    public double AudioDelaySeconds { get; init; }
    public double SubtitleDelaySeconds { get; init; }
    public string AspectRatio { get; init; } = "auto";
    public bool ShowBrowserPanel { get; init; }
    public bool ShowPlaylistPanel { get; init; }
    public bool ResumeEnabled { get; init; } = true;
    public PlaybackWindowMode WindowMode { get; init; } = PlaybackWindowMode.Standard;
    public SubtitleRenderMode LastNonOffSubtitleRenderMode { get; init; } = SubtitleRenderMode.TranslationOnly;
    public bool ShowSubtitleSource { get; init; }
}

public sealed record ShellLayoutPreferencesChange(
    bool ShowBrowserPanel,
    bool ShowPlaylistPanel,
    PlaybackWindowMode WindowMode);

public sealed record ShellPlaybackDefaultsChange(
    HardwareDecodingMode HardwareDecodingMode,
    double PlaybackRate,
    double AudioDelaySeconds,
    double SubtitleDelaySeconds,
    string AspectRatio);

public sealed record ShellSubtitlePresentationChange(
    SubtitleRenderMode SubtitleRenderMode,
    SubtitleStyleSettings SubtitleStyle);

public sealed record ShellAudioStateChange(
    double VolumeLevel,
    bool IsMuted);

public sealed record ShellShortcutProfileChange(ShortcutProfile ShortcutProfile);

public sealed record ShellResumeEnabledChange(bool ResumeEnabled);

public sealed record ShellPinnedRootsChange(IReadOnlyList<string> PinnedRoots);

public sealed class ShellPreferencesService : IShellPreferencesService
{
    private readonly SettingsFacade _settingsFacade;

    public ShellPreferencesService(SettingsFacade? settingsFacade = null)
    {
        _settingsFacade = settingsFacade ?? new SettingsFacade();
        Current = MapToSnapshot(LoadSettings());
    }

    public ShellPreferencesSnapshot Current { get; private set; }

    public event Action<ShellPreferencesSnapshot>? SnapshotChanged;

    public ShellPreferencesSnapshot ApplyLayoutChange(ShellLayoutPreferencesChange change)
    {
        var updated = ToSettings(Current) with
        {
            ShowBrowserPanel = change.ShowBrowserPanel,
            ShowPlaylistPanel = change.ShowPlaylistPanel,
            WindowMode = change.WindowMode
        };

        return Persist(updated);
    }

    public ShellPreferencesSnapshot ApplyPlaybackDefaultsChange(ShellPlaybackDefaultsChange change)
    {
        var updated = ToSettings(Current) with
        {
            HardwareDecodingMode = change.HardwareDecodingMode,
            DefaultPlaybackRate = change.PlaybackRate,
            AudioDelaySeconds = change.AudioDelaySeconds,
            SubtitleDelaySeconds = change.SubtitleDelaySeconds,
            AspectRatioOverride = NormalizeAspectRatio(change.AspectRatio)
        };

        return Persist(updated);
    }

    public ShellPreferencesSnapshot ApplySubtitlePresentationChange(ShellSubtitlePresentationChange change)
    {
        ArgumentNullException.ThrowIfNull(change.SubtitleStyle);

        var updated = ToSettings(Current) with
        {
            SubtitleRenderMode = change.SubtitleRenderMode,
            SubtitleStyle = change.SubtitleStyle
        };

        return Persist(updated);
    }

    public ShellPreferencesSnapshot ApplyAudioStateChange(ShellAudioStateChange change)
    {
        var updated = ToSettings(Current) with
        {
            VolumeLevel = Math.Clamp(change.VolumeLevel, 0, 1),
            IsMuted = change.IsMuted
        };

        return Persist(updated);
    }

    public ShellPreferencesSnapshot ApplyShortcutProfileChange(ShellShortcutProfileChange change)
    {
        ArgumentNullException.ThrowIfNull(change.ShortcutProfile);

        var updated = ToSettings(Current) with
        {
            ShortcutProfile = change.ShortcutProfile
        };

        return Persist(updated);
    }

    public ShellPreferencesSnapshot ApplyResumeEnabledChange(ShellResumeEnabledChange change)
    {
        var updated = ToSettings(Current) with
        {
            ResumeEnabled = change.ResumeEnabled
        };

        return Persist(updated);
    }

    public ShellPreferencesSnapshot ApplyPinnedRootsChange(ShellPinnedRootsChange change)
    {
        ArgumentNullException.ThrowIfNull(change.PinnedRoots);

        var updated = ToSettings(Current) with
        {
            PinnedRoots = change.PinnedRoots
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        return Persist(updated);
    }

    private ShellPreferencesSnapshot Persist(AppPlayerSettings updated)
    {
        _settingsFacade.Save(updated);
        Current = MapToSnapshot(updated);
        SnapshotChanged?.Invoke(Current);
        return Current;
    }

    private AppPlayerSettings LoadSettings()
    {
        var settings = _settingsFacade.Load();
        if (settings.PinnedRoots.Count == 0)
        {
            var defaultVideosPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var pinnedRoots = Directory.Exists(defaultVideosPath) ? new List<string> { defaultVideosPath } : [];
            settings = settings with
            {
                PinnedRoots = pinnedRoots,
                ShowBrowserPanel = pinnedRoots.Count > 0,
                ShowPlaylistPanel = true
            };
        }

        return settings;
    }

    private static ShellPreferencesSnapshot MapToSnapshot(AppPlayerSettings settings)
    {
        var subtitleRenderMode = settings.SubtitleRenderMode;
        return new ShellPreferencesSnapshot
        {
            HardwareDecodingMode = settings.HardwareDecodingMode,
            SubtitleRenderMode = subtitleRenderMode,
            SubtitleStyle = settings.SubtitleStyle,
            ShortcutProfile = settings.ShortcutProfile,
            PinnedRoots = settings.PinnedRoots.ToArray(),
            VolumeLevel = Math.Clamp(settings.VolumeLevel, 0, 1),
            IsMuted = settings.IsMuted,
            PlaybackRate = settings.DefaultPlaybackRate,
            AudioDelaySeconds = settings.AudioDelaySeconds,
            SubtitleDelaySeconds = settings.SubtitleDelaySeconds,
            AspectRatio = NormalizeAspectRatio(settings.AspectRatioOverride),
            ShowBrowserPanel = settings.ShowBrowserPanel,
            ShowPlaylistPanel = settings.ShowPlaylistPanel,
            ResumeEnabled = settings.ResumeEnabled,
            WindowMode = settings.WindowMode,
            LastNonOffSubtitleRenderMode = subtitleRenderMode == SubtitleRenderMode.Off
                ? SubtitleRenderMode.TranslationOnly
                : subtitleRenderMode,
            ShowSubtitleSource = subtitleRenderMode is SubtitleRenderMode.SourceOnly or SubtitleRenderMode.Dual
        };
    }

    private static AppPlayerSettings ToSettings(ShellPreferencesSnapshot snapshot)
    {
        return new AppPlayerSettings
        {
            HardwareDecodingMode = snapshot.HardwareDecodingMode,
            SubtitleRenderMode = snapshot.SubtitleRenderMode,
            SubtitleStyle = snapshot.SubtitleStyle,
            ShortcutProfile = snapshot.ShortcutProfile,
            PinnedRoots = snapshot.PinnedRoots.ToList(),
            VolumeLevel = Math.Clamp(snapshot.VolumeLevel, 0, 1),
            IsMuted = snapshot.IsMuted,
            DefaultPlaybackRate = snapshot.PlaybackRate,
            AudioDelaySeconds = snapshot.AudioDelaySeconds,
            SubtitleDelaySeconds = snapshot.SubtitleDelaySeconds,
            AspectRatioOverride = NormalizeAspectRatio(snapshot.AspectRatio),
            ShowBrowserPanel = snapshot.ShowBrowserPanel,
            ShowPlaylistPanel = snapshot.ShowPlaylistPanel,
            ResumeEnabled = snapshot.ResumeEnabled,
            WindowMode = snapshot.WindowMode
        };
    }

    private static string NormalizeAspectRatio(string? aspectRatio)
        => string.IsNullOrWhiteSpace(aspectRatio) ? "auto" : aspectRatio;
}
