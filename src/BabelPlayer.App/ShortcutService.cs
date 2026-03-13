using System.Collections.ObjectModel;

namespace BabelPlayer.App;

public sealed class ShortcutService
{
    private static readonly IReadOnlyList<ShortcutActionDefinition> _supportedActions =
    [
        new("play_pause", "Play / Pause", "Toggle playback."),
        new("seek_back_small", "Seek Back 5s", "Seek backward by five seconds."),
        new("seek_forward_small", "Seek Forward 5s", "Seek forward by five seconds."),
        new("seek_back_large", "Seek Back 15s", "Seek backward by fifteen seconds."),
        new("seek_forward_large", "Seek Forward 15s", "Seek forward by fifteen seconds."),
        new("previous_frame", "Previous Frame", "Step backward one frame."),
        new("next_frame", "Next Frame", "Step forward one frame."),
        new("speed_down", "Speed Down", "Reduce playback speed."),
        new("speed_up", "Speed Up", "Increase playback speed."),
        new("speed_reset", "Reset Speed", "Return playback speed to 1.0x."),
        new("subtitle_toggle", "Toggle Subtitles", "Show or hide subtitles."),
        new("translation_toggle", "Toggle Translation", "Enable or disable translation for the current video."),
        new("subtitle_delay_back", "Subtitle Delay Back", "Move subtitles 50 ms earlier."),
        new("subtitle_delay_forward", "Subtitle Delay Forward", "Move subtitles 50 ms later."),
        new("audio_delay_back", "Audio Delay Back", "Move audio 50 ms earlier."),
        new("audio_delay_forward", "Audio Delay Forward", "Move audio 50 ms later."),
        new("fullscreen", "Fullscreen", "Toggle fullscreen mode."),
        new("pip", "Picture in Picture", "Toggle picture-in-picture mode."),
        new("next_item", "Next Item", "Move to the next playlist item."),
        new("previous_item", "Previous Item", "Move to the previous playlist item."),
        new("mute", "Mute", "Toggle audio mute.")
    ];

    public static IReadOnlyList<ShortcutActionDefinition> SupportedActions => _supportedActions;

    public ShortcutGesture ParseGesture(string gesture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gesture);

        var tokens = gesture
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim())
            .ToArray();

        if (tokens.Length == 0)
        {
            throw new FormatException("Shortcut gesture cannot be empty.");
        }

        var key = tokens[^1];
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new FormatException("Shortcut gesture must end with a key.");
        }

        return new ShortcutGesture(
            new ReadOnlyCollection<string>(tokens[..^1]),
            key);
    }

    public IReadOnlyList<ShortcutConflict> FindConflicts(ShellShortcutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Dictionary<string, string> gestureToAction = new(StringComparer.OrdinalIgnoreCase);
        List<ShortcutConflict> conflicts = [];

        foreach (var binding in profile.Bindings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(binding.Value))
            {
                continue;
            }

            var normalizedGesture = Normalize(binding.Value);
            if (gestureToAction.TryGetValue(normalizedGesture, out var existingAction))
            {
                conflicts.Add(new ShortcutConflict(existingAction, binding.Key, normalizedGesture));
                continue;
            }

            gestureToAction[normalizedGesture] = binding.Key;
        }

        return conflicts;
    }

    public string Normalize(string gesture)
    {
        var parsed = ParseGesture(gesture);
        var modifiers = parsed.Modifiers
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);

        return string.Join('+', modifiers.Append(parsed.Key.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}

public sealed record ShortcutGesture(IReadOnlyList<string> Modifiers, string Key);

public sealed record ShortcutConflict(string ExistingAction, string ConflictingAction, string Gesture);

public sealed record ShortcutActionDefinition(string CommandId, string DisplayName, string Description);
