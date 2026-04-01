using System;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

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

    /// <summary>
    /// Returns whether this provider is ready to run. Override to check model downloads,
    /// CLI availability, or API key presence. Default: always ready.
    /// </summary>
    ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
        => ProviderReadiness.Ready;

    /// <summary>
    /// Downloads or prepares any required assets. Returns true when the provider is ready.
    /// Override if <see cref="CheckReadiness"/> can return <c>RequiresModelDownload: true</c>.
    /// Default: no-op, returns true.
    /// </summary>
    Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default)
        => Task.FromResult(true);
}
