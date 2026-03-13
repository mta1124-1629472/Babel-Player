using BabelPlayer.Core;

namespace BabelPlayer.App;

public interface IShortcutProfileService
{
    event Action<ShortcutProfileSnapshot>? SnapshotChanged;

    ShortcutProfileSnapshot Current { get; }

    ShortcutProfileValidationResult ValidateProfile(ShortcutProfile profile);

    ShortcutProfileNormalizationResult NormalizeProfile(ShortcutProfile profile);

    ShortcutProfileSnapshot ApplyShortcutProfileChange(ShortcutProfile profile);
}

public sealed record ShortcutBindingSnapshot(string CommandId, string NormalizedGesture);

public sealed record ShortcutProfileSnapshot
{
    public ShortcutProfile Profile { get; init; } = ShortcutProfile.CreateDefault();
    public IReadOnlyList<ShortcutBindingSnapshot> NormalizedBindings { get; init; } = [];
    public IReadOnlyList<ShortcutConflict> Conflicts { get; init; } = [];
    public IReadOnlyList<string> UnsupportedCommandIds { get; init; } = [];
    public IReadOnlyList<string> InvalidCommandIds { get; init; } = [];
    public IReadOnlyList<ShortcutActionDefinition> SupportedActions { get; init; } = ShortcutService.SupportedActions;
}

public sealed record ShortcutProfileValidationResult(
    bool IsValid,
    IReadOnlyList<ShortcutConflict> Conflicts,
    IReadOnlyList<string> UnsupportedCommandIds,
    IReadOnlyList<string> InvalidCommandIds);

public sealed record ShortcutProfileNormalizationResult(
    ShortcutProfile Profile,
    IReadOnlyList<ShortcutBindingSnapshot> NormalizedBindings,
    IReadOnlyList<string> UnsupportedCommandIds,
    IReadOnlyList<string> InvalidCommandIds);

public sealed class ShortcutProfileService : IShortcutProfileService, IDisposable
{
    private readonly IShellPreferencesService _shellPreferencesService;
    private readonly ShortcutService _shortcutService;
    private readonly HashSet<string> _supportedCommandIds;

    public ShortcutProfileService(
        IShellPreferencesService shellPreferencesService,
        ShortcutService? shortcutService = null)
    {
        _shellPreferencesService = shellPreferencesService;
        _shortcutService = shortcutService ?? new ShortcutService();
        _supportedCommandIds = ShortcutService.SupportedActions
            .Select(action => action.CommandId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Current = BuildSnapshot(_shellPreferencesService.Current.ShortcutProfile);
        _shellPreferencesService.SnapshotChanged += HandleShellPreferencesChanged;
    }

    public event Action<ShortcutProfileSnapshot>? SnapshotChanged;

    public ShortcutProfileSnapshot Current { get; private set; }

    public ShortcutProfileValidationResult ValidateProfile(ShortcutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var normalization = NormalizeProfile(profile);
        var normalizedProfile = new ShortcutProfile
        {
            Bindings = normalization.NormalizedBindings.ToDictionary(
                binding => binding.CommandId,
                binding => binding.NormalizedGesture,
                StringComparer.OrdinalIgnoreCase)
        };
        var conflicts = _shortcutService.FindConflicts(normalizedProfile);
        return new ShortcutProfileValidationResult(
            conflicts.Count == 0
            && normalization.UnsupportedCommandIds.Count == 0
            && normalization.InvalidCommandIds.Count == 0,
            conflicts,
            normalization.UnsupportedCommandIds,
            normalization.InvalidCommandIds);
    }

    public ShortcutProfileNormalizationResult NormalizeProfile(ShortcutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        List<ShortcutBindingSnapshot> normalizedBindings = [];
        List<string> unsupportedCommandIds = [];
        List<string> invalidCommandIds = [];

        foreach (var binding in profile.Bindings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!_supportedCommandIds.Contains(binding.Key))
            {
                unsupportedCommandIds.Add(binding.Key);
                continue;
            }

            if (string.IsNullOrWhiteSpace(binding.Value))
            {
                continue;
            }

            try
            {
                var normalizedGesture = _shortcutService.Normalize(binding.Value);
                if (!IsSupportedGesture(normalizedGesture))
                {
                    invalidCommandIds.Add(binding.Key);
                    continue;
                }

                normalizedBindings.Add(new ShortcutBindingSnapshot(
                    binding.Key,
                    normalizedGesture));
            }
            catch (FormatException)
            {
                invalidCommandIds.Add(binding.Key);
            }
        }

        return new ShortcutProfileNormalizationResult(
            profile,
            normalizedBindings,
            unsupportedCommandIds,
            invalidCommandIds);
    }

    public ShortcutProfileSnapshot ApplyShortcutProfileChange(ShortcutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _shellPreferencesService.ApplyShortcutProfileChange(new ShellShortcutProfileChange(profile));
        return Current;
    }

    public void Dispose()
    {
        _shellPreferencesService.SnapshotChanged -= HandleShellPreferencesChanged;
    }

    private void HandleShellPreferencesChanged(ShellPreferencesSnapshot snapshot)
    {
        Current = BuildSnapshot(snapshot.ShortcutProfile);
        SnapshotChanged?.Invoke(Current);
    }

    private ShortcutProfileSnapshot BuildSnapshot(ShortcutProfile profile)
    {
        var normalization = NormalizeProfile(profile);
        var normalizedProfile = new ShortcutProfile
        {
            Bindings = normalization.NormalizedBindings.ToDictionary(
                binding => binding.CommandId,
                binding => binding.NormalizedGesture,
                StringComparer.OrdinalIgnoreCase)
        };
        return new ShortcutProfileSnapshot
        {
            Profile = profile,
            NormalizedBindings = normalization.NormalizedBindings,
            UnsupportedCommandIds = normalization.UnsupportedCommandIds,
            InvalidCommandIds = normalization.InvalidCommandIds,
            Conflicts = _shortcutService.FindConflicts(normalizedProfile),
            SupportedActions = ShortcutService.SupportedActions
        };
    }

    private static bool IsSupportedGesture(string gesture)
    {
        var tokens = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        foreach (var modifier in tokens[..^1])
        {
            if (!modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                && !modifier.Equals("Control", StringComparison.OrdinalIgnoreCase)
                && !modifier.Equals("Alt", StringComparison.OrdinalIgnoreCase)
                && !modifier.Equals("Menu", StringComparison.OrdinalIgnoreCase)
                && !modifier.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return IsSupportedKeyToken(tokens[^1]);
    }

    private static bool IsSupportedKeyToken(string keyToken)
    {
        if (string.IsNullOrWhiteSpace(keyToken))
        {
            return false;
        }

        return keyToken.Trim() switch
        {
            "Space" or "Left" or "Right" or "PageUp" or "PageDown" or "F11" or "OemMinus" or "OemPlus" or "OemComma" or "OemPeriod" or "D0"
                => true,
            _ => keyToken.Length == 1 && char.IsLetterOrDigit(keyToken[0])
        };
    }
}
