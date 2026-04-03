namespace Babel.Player.Models;

/// <summary>
/// Tracks a compute type downgrade decision (requested vs. effective) with reason and context.
/// Used for explicit, auditable logging and UI projection of fallback events.
/// </summary>
public sealed record ComputeDowngradeEvent(
    /// <summary>The originally requested compute type (e.g., "float8", "float16", "int8", "cpu").</summary>
    string RequestedComputeType,

    /// <summary>The effective/resolved compute type after validation (e.g., "float16" if FP8 unsupported).</summary>
    string EffectiveComputeType,

    /// <summary>Human-readable reason for the downgrade (e.g., "FP8 not supported by Whisper on this device").</summary>
    string DowngradeReason,

    /// <summary>Pipeline stage affected: "transcription", "translation", "tts".</summary>
    string Stage,

    /// <summary>ISO 8601 timestamp when the downgrade decision was made.</summary>
    string Timestamp = "")
{
    /// <summary>
    /// Initializes a new ComputeDowngradeEvent with auto-generated timestamp.
    /// </summary>
    public ComputeDowngradeEvent(
        string requestedComputeType,
        string effectiveComputeType,
        string downgradeReason,
        string stage)
        : this(
            requestedComputeType,
            effectiveComputeType,
            downgradeReason,
            stage,
            DateTime.UtcNow.ToString("O"))
    {
    }

    /// <summary>
    /// Returns true if effective type differs from requested type.
    /// </summary>
    public bool IsDowngraded => RequestedComputeType != EffectiveComputeType;

    /// <summary>
    /// User-facing summary: "Requested X but using Y (reason)".
    /// </summary>
    public string Summary =>
        IsDowngraded
            ? $"Requested {RequestedComputeType} but using {EffectiveComputeType} ({DowngradeReason})"
            : $"Using {EffectiveComputeType}";
}
