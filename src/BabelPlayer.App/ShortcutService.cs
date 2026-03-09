using System.Collections.ObjectModel;
using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class ShortcutService
{
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

    public IReadOnlyList<ShortcutConflict> FindConflicts(ShortcutProfile profile)
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
