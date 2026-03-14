using BabelPlayer.Core;

namespace BabelPlayer.App;

public enum ShellMediaTrackKind
{
    Video,
    Audio,
    Subtitle
}

public enum ShellSubtitleRenderMode
{
    Off,
    SourceOnly,
    TranslationOnly,
    Dual,
    TranscribeOnly
}

public enum ShellHardwareDecodingMode
{
    AutoSafe,
    D3D11,
    Nvdec,
    Software
}

public enum ShellPlaybackWindowMode
{
    Standard,
    Borderless,
    PictureInPicture,
    Fullscreen
}

public sealed class ShellMediaTrack
{
    public int Id { get; init; }
    public int? FfIndex { get; init; }
    public ShellMediaTrackKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Language { get; init; } = "und";
    public string Codec { get; init; } = string.Empty;
    public bool IsEmbedded { get; init; }
    public bool IsSelected { get; init; }
    public bool IsTextBased { get; init; }
}

public sealed record ShellSubtitleStyle
{
    public double SourceFontSize { get; init; } = 28;
    public double TranslationFontSize { get; init; } = 30;
    public string SourceForegroundHex { get; init; } = "#F1F6FB";
    public string TranslationForegroundHex { get; init; } = "#FFFFFF";
    public double BackgroundOpacity { get; init; } = 0.78;
    public double BottomMargin { get; init; } = 18;
    public double DualSpacing { get; init; } = 8;
}

public sealed record ShellPlaybackStateSnapshot
{
    public string? Path { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public int VideoWidth { get; init; }
    public int VideoHeight { get; init; }
    public int VideoDisplayWidth { get; init; }
    public int VideoDisplayHeight { get; init; }
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

public sealed class ShellShortcutProfile
{
    public Dictionary<string, string> Bindings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static ShellShortcutProfile CreateDefault()
    {
        return new ShellShortcutProfile
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

public sealed record ShellPlaylistItem
{
    public string Path { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsDirectorySeed { get; init; }
}

public sealed class ShellSubtitleCue
{
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string SourceText { get; init; } = string.Empty;
    public string? SourceLanguage { get; set; }
    public string? TranslatedText { get; set; }
    public string DisplayText => string.IsNullOrWhiteSpace(TranslatedText) ? SourceText : TranslatedText;
}

public sealed class ShellRuntimeInstallProgress
{
    public string Stage { get; init; } = string.Empty;
    public long BytesTransferred { get; init; }
    public long? TotalBytes { get; init; }
    public int? ItemsCompleted { get; init; }
    public int? TotalItems { get; init; }
    public double? ProgressRatio =>
        TotalBytes is > 0
            ? (double)BytesTransferred / TotalBytes.Value
            : TotalItems is > 0 && ItemsCompleted is not null
                ? (double)ItemsCompleted.Value / TotalItems.Value
                : null;
}

internal static class ShellBoundaryMapper
{
    public static ShellHardwareDecodingMode ToShell(this HardwareDecodingMode mode)
        => mode switch
        {
            HardwareDecodingMode.AutoSafe => ShellHardwareDecodingMode.AutoSafe,
            HardwareDecodingMode.D3D11 => ShellHardwareDecodingMode.D3D11,
            HardwareDecodingMode.Nvdec => ShellHardwareDecodingMode.Nvdec,
            HardwareDecodingMode.Software => ShellHardwareDecodingMode.Software,
            _ => ShellHardwareDecodingMode.AutoSafe
        };

    public static HardwareDecodingMode ToCore(this ShellHardwareDecodingMode mode)
        => mode switch
        {
            ShellHardwareDecodingMode.AutoSafe => HardwareDecodingMode.AutoSafe,
            ShellHardwareDecodingMode.D3D11 => HardwareDecodingMode.D3D11,
            ShellHardwareDecodingMode.Nvdec => HardwareDecodingMode.Nvdec,
            ShellHardwareDecodingMode.Software => HardwareDecodingMode.Software,
            _ => HardwareDecodingMode.AutoSafe
        };

    public static ShellPlaybackWindowMode ToShell(this PlaybackWindowMode mode)
        => mode switch
        {
            PlaybackWindowMode.Standard => ShellPlaybackWindowMode.Standard,
            PlaybackWindowMode.Borderless => ShellPlaybackWindowMode.Borderless,
            PlaybackWindowMode.PictureInPicture => ShellPlaybackWindowMode.PictureInPicture,
            PlaybackWindowMode.Fullscreen => ShellPlaybackWindowMode.Fullscreen,
            _ => ShellPlaybackWindowMode.Standard
        };

    public static PlaybackWindowMode ToCore(this ShellPlaybackWindowMode mode)
        => mode switch
        {
            ShellPlaybackWindowMode.Standard => PlaybackWindowMode.Standard,
            ShellPlaybackWindowMode.Borderless => PlaybackWindowMode.Borderless,
            ShellPlaybackWindowMode.PictureInPicture => PlaybackWindowMode.PictureInPicture,
            ShellPlaybackWindowMode.Fullscreen => PlaybackWindowMode.Fullscreen,
            _ => PlaybackWindowMode.Standard
        };

    public static ShellSubtitleRenderMode ToShell(this SubtitleRenderMode mode)
        => mode switch
        {
            SubtitleRenderMode.Off => ShellSubtitleRenderMode.Off,
            SubtitleRenderMode.SourceOnly => ShellSubtitleRenderMode.SourceOnly,
            SubtitleRenderMode.TranslationOnly => ShellSubtitleRenderMode.TranslationOnly,
            SubtitleRenderMode.Dual => ShellSubtitleRenderMode.Dual,
            SubtitleRenderMode.TranscribeOnly => ShellSubtitleRenderMode.TranscribeOnly,
            _ => ShellSubtitleRenderMode.Off
        };

    public static SubtitleRenderMode ToCore(this ShellSubtitleRenderMode mode)
        => mode switch
        {
            ShellSubtitleRenderMode.Off => SubtitleRenderMode.Off,
            ShellSubtitleRenderMode.SourceOnly => SubtitleRenderMode.SourceOnly,
            ShellSubtitleRenderMode.TranslationOnly => SubtitleRenderMode.TranslationOnly,
            ShellSubtitleRenderMode.Dual => SubtitleRenderMode.Dual,
            ShellSubtitleRenderMode.TranscribeOnly => SubtitleRenderMode.TranscribeOnly,
            _ => SubtitleRenderMode.Off
        };

    public static ShellMediaTrack ToShell(this MediaTrackInfo track)
    {
        return new ShellMediaTrack
        {
            Id = track.Id,
            FfIndex = track.FfIndex,
            Kind = track.Kind switch
            {
                MediaTrackKind.Video => ShellMediaTrackKind.Video,
                MediaTrackKind.Audio => ShellMediaTrackKind.Audio,
                MediaTrackKind.Subtitle => ShellMediaTrackKind.Subtitle,
                _ => ShellMediaTrackKind.Subtitle
            },
            Title = track.Title,
            Language = track.Language,
            Codec = track.Codec,
            IsEmbedded = track.IsEmbedded,
            IsSelected = track.IsSelected,
            IsTextBased = track.IsTextBased
        };
    }

    public static MediaTrackInfo ToCore(this ShellMediaTrack track)
    {
        return new MediaTrackInfo
        {
            Id = track.Id,
            FfIndex = track.FfIndex,
            Kind = track.Kind switch
            {
                ShellMediaTrackKind.Video => MediaTrackKind.Video,
                ShellMediaTrackKind.Audio => MediaTrackKind.Audio,
                ShellMediaTrackKind.Subtitle => MediaTrackKind.Subtitle,
                _ => MediaTrackKind.Subtitle
            },
            Title = track.Title,
            Language = track.Language,
            Codec = track.Codec,
            IsEmbedded = track.IsEmbedded,
            IsSelected = track.IsSelected,
            IsTextBased = track.IsTextBased
        };
    }

    public static ShellSubtitleStyle ToShell(this SubtitleStyleSettings style)
    {
        return new ShellSubtitleStyle
        {
            SourceFontSize = style.SourceFontSize,
            TranslationFontSize = style.TranslationFontSize,
            SourceForegroundHex = style.SourceForegroundHex,
            TranslationForegroundHex = style.TranslationForegroundHex,
            BackgroundOpacity = style.BackgroundOpacity,
            BottomMargin = style.BottomMargin,
            DualSpacing = style.DualSpacing
        };
    }

    public static SubtitleStyleSettings ToCore(this ShellSubtitleStyle style)
    {
        return new SubtitleStyleSettings
        {
            SourceFontSize = style.SourceFontSize,
            TranslationFontSize = style.TranslationFontSize,
            SourceForegroundHex = style.SourceForegroundHex,
            TranslationForegroundHex = style.TranslationForegroundHex,
            BackgroundOpacity = style.BackgroundOpacity,
            BottomMargin = style.BottomMargin,
            DualSpacing = style.DualSpacing
        };
    }

    public static ShellPlaybackStateSnapshot ToShell(this PlaybackStateSnapshot snapshot)
    {
        return new ShellPlaybackStateSnapshot
        {
            Path = snapshot.Path,
            Position = snapshot.Position,
            Duration = snapshot.Duration,
            VideoWidth = snapshot.VideoWidth,
            VideoHeight = snapshot.VideoHeight,
            VideoDisplayWidth = snapshot.VideoDisplayWidth,
            VideoDisplayHeight = snapshot.VideoDisplayHeight,
            IsPaused = snapshot.IsPaused,
            IsMuted = snapshot.IsMuted,
            Volume = snapshot.Volume,
            Speed = snapshot.Speed,
            HasVideo = snapshot.HasVideo,
            HasAudio = snapshot.HasAudio,
            IsSeekable = snapshot.IsSeekable,
            ActiveHardwareDecoder = snapshot.ActiveHardwareDecoder,
            PlaylistIndex = snapshot.PlaylistIndex,
            PlaylistCount = snapshot.PlaylistCount
        };
    }

    public static PlaybackStateSnapshot ToCore(this ShellPlaybackStateSnapshot snapshot)
    {
        return new PlaybackStateSnapshot
        {
            Path = snapshot.Path,
            Position = snapshot.Position,
            Duration = snapshot.Duration,
            VideoWidth = snapshot.VideoWidth,
            VideoHeight = snapshot.VideoHeight,
            VideoDisplayWidth = snapshot.VideoDisplayWidth,
            VideoDisplayHeight = snapshot.VideoDisplayHeight,
            IsPaused = snapshot.IsPaused,
            IsMuted = snapshot.IsMuted,
            Volume = snapshot.Volume,
            Speed = snapshot.Speed,
            HasVideo = snapshot.HasVideo,
            HasAudio = snapshot.HasAudio,
            IsSeekable = snapshot.IsSeekable,
            ActiveHardwareDecoder = snapshot.ActiveHardwareDecoder,
            PlaylistIndex = snapshot.PlaylistIndex,
            PlaylistCount = snapshot.PlaylistCount
        };
    }

    public static ShellRuntimeInstallProgress ToShell(this RuntimeInstallProgress progress)
    {
        return new ShellRuntimeInstallProgress
        {
            Stage = progress.Stage,
            BytesTransferred = progress.BytesTransferred,
            TotalBytes = progress.TotalBytes,
            ItemsCompleted = progress.ItemsCompleted,
            TotalItems = progress.TotalItems
        };
    }

    public static ShellPlaylistItem ToShell(this PlaylistItem item)
    {
        return new ShellPlaylistItem
        {
            Path = item.Path,
            DisplayName = item.DisplayName,
            IsDirectorySeed = item.IsDirectorySeed
        };
    }

    public static PlaylistItem ToCore(this ShellPlaylistItem item)
    {
        return new PlaylistItem
        {
            Path = item.Path,
            DisplayName = item.DisplayName,
            IsDirectorySeed = item.IsDirectorySeed
        };
    }

    public static ShellSubtitleCue ToShell(this SubtitleCue cue)
    {
        return new ShellSubtitleCue
        {
            Start = cue.Start,
            End = cue.End,
            SourceText = cue.SourceText,
            SourceLanguage = cue.SourceLanguage,
            TranslatedText = cue.TranslatedText
        };
    }

    public static SubtitleCue ToCore(this ShellSubtitleCue cue)
    {
        return new SubtitleCue
        {
            Start = cue.Start,
            End = cue.End,
            SourceText = cue.SourceText,
            SourceLanguage = cue.SourceLanguage,
            TranslatedText = cue.TranslatedText
        };
    }

    public static ShellShortcutProfile ToShell(this ShortcutProfile profile)
    {
        return new ShellShortcutProfile
        {
            Bindings = new Dictionary<string, string>(profile.Bindings, StringComparer.OrdinalIgnoreCase)
        };
    }

    public static ShortcutProfile ToCore(this ShellShortcutProfile profile)
    {
        return new ShortcutProfile
        {
            Bindings = new Dictionary<string, string>(profile.Bindings, StringComparer.OrdinalIgnoreCase)
        };
    }
}
