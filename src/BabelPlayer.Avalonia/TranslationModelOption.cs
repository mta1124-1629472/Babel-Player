using Avalonia.Media;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public sealed class TranslationModelOption
{
    private static readonly IBrush ReadyBackground = new SolidColorBrush(Color.Parse("#1D5F3A"));
    private static readonly IBrush ReadyForeground = new SolidColorBrush(Color.Parse("#D7FFE7"));
    private static readonly IBrush NeedsKeyBackground = new SolidColorBrush(Color.Parse("#7A2E14"));
    private static readonly IBrush NeedsKeyForeground = new SolidColorBrush(Color.Parse("#FFD7CC"));
    private static readonly IBrush NeedsRuntimeBackground = new SolidColorBrush(Color.Parse("#6A560F"));
    private static readonly IBrush NeedsRuntimeForeground = new SolidColorBrush(Color.Parse("#FFF0B8"));

    public required TranslationModelSelection Selection { get; init; }

    public required CredentialSelectionAvailability Availability { get; init; }

    public string Key => Selection.Key;

    public string DisplayName => Selection.DisplayName;

    public string AvailabilityHint => Availability.Hint ?? string.Empty;

    public string BadgeText => Availability switch
    {
        { IsAvailable: true } => "Ready",
        { RequiresRuntimeBootstrap: true } => "Needs llama.cpp",
        { RequiresCredentials: true } => "Needs key",
        _ => "Unavailable"
    };

    public IBrush BadgeBackground => Availability switch
    {
        { IsAvailable: true } => ReadyBackground,
        { RequiresRuntimeBootstrap: true } => NeedsRuntimeBackground,
        _ => NeedsKeyBackground
    };

    public IBrush BadgeForeground => Availability switch
    {
        { IsAvailable: true } => ReadyForeground,
        { RequiresRuntimeBootstrap: true } => NeedsRuntimeForeground,
        _ => NeedsKeyForeground
    };
}
