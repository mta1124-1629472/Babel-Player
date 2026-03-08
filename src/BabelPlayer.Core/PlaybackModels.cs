using System.Text.Json.Serialization;

namespace BabelPlayer.Core;

public enum MediaTrackKind
{
    Video,
    Audio,
    Subtitle
}

public enum SubtitleRenderMode
{
    Off,
    SourceOnly,
    TranslationOnly,
    Dual
}

public enum HardwareDecodingMode
{
    AutoSafe,
    D3D11,
    Nvdec,
    Software
}

public enum PlaybackWindowMode
{
    Standard,
    Borderless,
    PictureInPicture
}

public sealed class MediaTrackInfo
{
    public int Id { get; init; }
    public int? FfIndex { get; init; }
    public MediaTrackKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Language { get; init; } = "und";
    public string Codec { get; init; } = string.Empty;
    public bool IsEmbedded { get; init; }
    public bool IsSelected { get; init; }
    public bool IsTextBased { get; init; }
}

public sealed record SubtitleStyleSettings
{
    public double SourceFontSize { get; init; } = 28;
    public double TranslationFontSize { get; init; } = 30;
    public string SourceForegroundHex { get; init; } = "#F1F6FB";
    public string TranslationForegroundHex { get; init; } = "#FFFFFF";
    public double BackgroundOpacity { get; init; } = 0.78;
    public double BottomMargin { get; init; } = 18;
    public double DualSpacing { get; init; } = 8;
}

public sealed class PlaylistItem
{
    public string Path { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsDirectorySeed { get; init; }
}

public sealed class PlaybackResumeEntry
{
    public string Path { get; init; } = string.Empty;
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PlaybackStateSnapshot
{
    public string? Path { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsPaused { get; init; }
    public bool IsMuted { get; init; }
    public double Volume { get; init; }
    public double Speed { get; init; } = 1.0;
    public bool HasVideo { get; init; }
    public bool HasAudio { get; init; }
    public bool IsSeekable { get; init; }
    public string ActiveHardwareDecoder { get; init; } = string.Empty;
    public int PlaylistIndex { get; init; } = -1;
    public int PlaylistCount { get; init; }
}

public sealed class ShortcutProfile
{
    public Dictionary<string, string> Bindings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static ShortcutProfile CreateDefault()
    {
        return new ShortcutProfile
        {
            Bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["play_pause"] = "Space",
                ["seek_back_small"] = "Left",
                ["seek_forward_small"] = "Right",
                ["seek_back_large"] = "Shift+Left",
                ["seek_forward_large"] = "Shift+Right",
                ["previous_frame"] = "Ctrl+Left",
                ["next_frame"] = "Ctrl+Right",
                ["speed_down"] = "OemMinus",
                ["speed_up"] = "OemPlus",
                ["speed_reset"] = "D0",
                ["subtitle_toggle"] = "S",
                ["translation_toggle"] = "T",
                ["subtitle_delay_back"] = "Ctrl+OemComma",
                ["subtitle_delay_forward"] = "Ctrl+OemPeriod",
                ["audio_delay_back"] = "Alt+OemComma",
                ["audio_delay_forward"] = "Alt+OemPeriod",
                ["fullscreen"] = "F11",
                ["pip"] = "P",
                ["next_item"] = "PageDown",
                ["previous_item"] = "PageUp",
                ["mute"] = "M"
            }
        };
    }
}

public sealed record AppPlayerSettings
{
    public HardwareDecodingMode HardwareDecodingMode { get; init; } = HardwareDecodingMode.AutoSafe;
    public SubtitleRenderMode SubtitleRenderMode { get; init; } = SubtitleRenderMode.TranslationOnly;
    public SubtitleStyleSettings SubtitleStyle { get; init; } = new();
    public ShortcutProfile ShortcutProfile { get; init; } = ShortcutProfile.CreateDefault();
    public List<string> PinnedRoots { get; init; } = [];
    public double DefaultPlaybackRate { get; init; } = 1.0;
    public double AudioDelaySeconds { get; init; }
    public double SubtitleDelaySeconds { get; init; }
    public string AspectRatioOverride { get; init; } = "auto";
    public bool ShowBrowserPanel { get; init; }
    public bool ShowPlaylistPanel { get; init; }
    public bool ResumeEnabled { get; init; } = true;
    public PlaybackWindowMode WindowMode { get; init; } = PlaybackWindowMode.Standard;
}
