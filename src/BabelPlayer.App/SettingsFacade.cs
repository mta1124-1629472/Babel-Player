using BabelPlayer.Core;

namespace BabelPlayer.App;

public class SettingsFacade
{
    public virtual AppPlayerSettings Load() => AppStateStore.LoadSettings();

    public virtual void Save(AppPlayerSettings settings) => AppStateStore.SaveSettings(settings);

    public IReadOnlyList<PlaybackResumeEntry> LoadResumeEntries() => AppStateStore.LoadResumeEntries();

    public void SaveResumeEntries(IReadOnlyList<PlaybackResumeEntry> entries) => AppStateStore.SaveResumeEntries(entries);

    public AppPlayerSettings UpdateLayout(AppPlayerSettings current, bool showBrowserPanel, bool showPlaylistPanel, PlaybackWindowMode windowMode)
    {
        return current with
        {
            ShowBrowserPanel = showBrowserPanel,
            ShowPlaylistPanel = showPlaylistPanel,
            WindowMode = windowMode
        };
    }

    public AppPlayerSettings UpdatePlaybackDefaults(
        AppPlayerSettings current,
        HardwareDecodingMode hardwareDecodingMode,
        double playbackRate,
        double audioDelaySeconds,
        double subtitleDelaySeconds,
        string aspectRatioOverride)
    {
        return current with
        {
            HardwareDecodingMode = hardwareDecodingMode,
            DefaultPlaybackRate = playbackRate,
            AudioDelaySeconds = audioDelaySeconds,
            SubtitleDelaySeconds = subtitleDelaySeconds,
            AspectRatioOverride = string.IsNullOrWhiteSpace(aspectRatioOverride) ? "auto" : aspectRatioOverride
        };
    }

    public AppPlayerSettings UpdateSubtitlePresentation(AppPlayerSettings current, SubtitleRenderMode mode, SubtitleStyleSettings subtitleStyle)
    {
        ArgumentNullException.ThrowIfNull(subtitleStyle);

        return current with
        {
            SubtitleRenderMode = mode,
            SubtitleStyle = subtitleStyle
        };
    }
}
