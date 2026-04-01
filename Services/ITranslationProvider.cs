using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Translates timed transcript segments into a target language.
/// Implementations are provider-specific (e.g. google-translate-free, nllb-200).
/// Provider configuration (model name, credentials) belongs in the constructor —
/// method signatures are uniform across all implementations.
/// </summary>
public interface ITranslationProvider
{
    /// <summary>
    /// Translates all segments in a transcript JSON and writes the result to
    /// a new translation JSON artifact.
    /// </summary>
    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Translates a single segment and writes the updated translation JSON artifact.
    /// Used for on-demand regeneration.
    /// </summary>
    Task<TranslationResult> TranslateSingleSegmentAsync(
        SingleSegmentTranslationRequest request,
        CancellationToken cancellationToken = default);
}
