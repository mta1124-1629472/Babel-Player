using System;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Transcribes audio into timed text segments.
/// Implementations are provider-specific (e.g. faster-whisper).
/// </summary>
public interface ITranscriptionProvider
{
    Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
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
