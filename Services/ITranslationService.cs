using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Translates timed transcript segments into a target language.
/// Implementations are provider-specific (e.g. google-translate-free, nllb-200).
/// Provider configuration (model name, credentials) belongs in the constructor —
/// method signatures are uniform across all implementations.
/// </summary>
public interface ITranslationService
{
    /// <summary>
    /// Translates all segments in a transcript JSON and writes the result to
    /// <paramref name="outputJsonPath"/> using the standard translation artifact contract.
    /// </summary>
    Task<TranslationResult> TranslateAsync(
        string transcriptJsonPath,
        string outputJsonPath,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-translates a single segment and updates its <c>translatedText</c> field
    /// in the existing translation JSON at <paramref name="translationJsonPath"/>.
    /// </summary>
    Task<TranslationResult> TranslateSingleSegmentAsync(
        string text,
        string segmentId,
        string translationJsonPath,
        string outputJsonPath,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
