using System.Runtime.InteropServices;
using BabelPlayer.Core;

namespace BabelPlayer.Playback.Mpv;

internal static class LibMpvNodeHelpers
{
    internal static string? GetNodeMapString(MpvNode node, string key)
    {
        var child = GetNodeMapValue(node, key);
        return child?.Format == MpvFormat.String && child.Value.Value.String != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(child.Value.Value.String)
            : null;
    }

    internal static int? GetNodeMapInt(MpvNode node, string key)
    {
        var child = GetNodeMapValue(node, key);
        return child?.Format switch
        {
            MpvFormat.Int64 => checked((int)child.Value.Value.Int64),
            MpvFormat.Double => checked((int)child.Value.Value.Double),
            _ => null
        };
    }

    internal static bool? GetNodeMapFlag(MpvNode node, string key)
    {
        var child = GetNodeMapValue(node, key);
        return child?.Format == MpvFormat.Flag ? child.Value.Value.Flag != 0 : null;
    }

    internal static IReadOnlyList<MediaTrackInfo> ParseTracks(MpvNode node, int? selectedAudio, int? selectedSubtitle)
    {
        if (node.Format != MpvFormat.NodeArray || node.Value.List == IntPtr.Zero)
        {
            return [];
        }

        var result = new List<MediaTrackInfo>();
        var list = Marshal.PtrToStructure<MpvNodeList>(node.Value.List);
        var nodeSize = Marshal.SizeOf<MpvNode>();
        for (var index = 0; index < list.Num; index++)
        {
            var itemPtr = IntPtr.Add(list.Values, index * nodeSize);
            var item = Marshal.PtrToStructure<MpvNode>(itemPtr);
            if (item.Format != MpvFormat.NodeMap || item.Value.List == IntPtr.Zero)
            {
                continue;
            }

            var id = GetNodeMapInt(item, "id");
            if (id is null)
            {
                continue;
            }

            var type = GetNodeMapString(item, "type");
            var kind = type switch
            {
                "audio" => MediaTrackKind.Audio,
                "sub" => MediaTrackKind.Subtitle,
                "video" => MediaTrackKind.Video,
                _ => MediaTrackKind.Video
            };

            var codec = GetNodeMapString(item, "codec") ?? string.Empty;
            var language = GetNodeMapString(item, "lang");
            result.Add(new MediaTrackInfo
            {
                Id = id.Value,
                FfIndex = GetNodeMapInt(item, "ff-index"),
                Kind = kind,
                Title = GetNodeMapString(item, "title") ?? string.Empty,
                Language = string.IsNullOrWhiteSpace(language) ? "und" : language,
                Codec = codec,
                IsEmbedded = true,
                IsSelected = kind switch
                {
                    MediaTrackKind.Audio => selectedAudio == id.Value,
                    MediaTrackKind.Subtitle => selectedSubtitle == id.Value,
                    _ => GetNodeMapFlag(item, "selected") ?? false
                },
                IsTextBased = kind != MediaTrackKind.Subtitle || IsTextSubtitleCodec(codec)
            });
        }

        return result;
    }

    private static MpvNode? GetNodeMapValue(MpvNode node, string key)
    {
        if (node.Format != MpvFormat.NodeMap || node.Value.List == IntPtr.Zero)
        {
            return null;
        }

        var list = Marshal.PtrToStructure<MpvNodeList>(node.Value.List);
        var nodeSize = Marshal.SizeOf<MpvNode>();
        for (var index = 0; index < list.Num; index++)
        {
            var keyPtr = Marshal.ReadIntPtr(list.Keys, index * IntPtr.Size);
            var currentKey = Marshal.PtrToStringUTF8(keyPtr);
            if (!string.Equals(currentKey, key, StringComparison.Ordinal))
            {
                continue;
            }

            var valuePtr = IntPtr.Add(list.Values, index * nodeSize);
            return Marshal.PtrToStructure<MpvNode>(valuePtr);
        }

        return null;
    }

    private static bool IsTextSubtitleCodec(string codec)
    {
        return codec.Contains("subrip", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("mov_text", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("ass", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("ssa", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("webvtt", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("text", StringComparison.OrdinalIgnoreCase);
    }
}
