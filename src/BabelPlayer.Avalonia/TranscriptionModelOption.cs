using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public sealed class TranscriptionModelOption
{
    public required TranscriptionModelSelection Selection { get; init; }

    public required CredentialSelectionAvailability Availability { get; init; }

    public string Key => Selection.Key;

    public string DisplayName => Selection.DisplayName;

    public bool ShowUnavailableIndicator => !Availability.IsAvailable && Availability.RequiresCredentials;

    public string AvailabilityHint => Availability.Hint ?? string.Empty;
}
