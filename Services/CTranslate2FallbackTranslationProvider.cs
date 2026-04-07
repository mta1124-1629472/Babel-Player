using System;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Wraps the CTranslate2 CPU translation provider and transparently falls back
/// to the NLLB (PyTorch) provider if CTranslate2 fails to initialise or run.
/// The <paramref name="onFallback"/> callback is invoked once when fallback occurs
/// so the coordinator can surface the note in the Active Config panel.
/// </summary>
internal sealed class CTranslate2FallbackTranslationProvider : ITranslationProvider
{
    private readonly ITranslationProvider _primary;
    private readonly ITranslationProvider _fallback;
    private readonly AppLog _log;
    private readonly Action<string> _onFallback;
    private bool _fallbackActive;

    public CTranslate2FallbackTranslationProvider(
        ITranslationProvider primary,
        ITranslationProvider fallback,
        AppLog log,
        Action<string> onFallback)
    {
        _primary   = primary;
        _fallback  = fallback;
        _log       = log;
        _onFallback = onFallback;
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_fallbackActive)
            return await _fallback.TranslateAsync(request, cancellationToken);

        var result = await _primary.TranslateAsync(request, cancellationToken);
        if (!result.Success && IsCTranslate2InitError(result.ErrorMessage))
        {
            ActivateFallback(result.ErrorMessage!);
            return await _fallback.TranslateAsync(request, cancellationToken);
        }
        return result;
    }

    public async Task<TranslationResult> TranslateSingleSegmentAsync(
        SingleSegmentTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_fallbackActive)
            return await _fallback.TranslateSingleSegmentAsync(request, cancellationToken);

        var result = await _primary.TranslateSingleSegmentAsync(request, cancellationToken);
        if (!result.Success && IsCTranslate2InitError(result.ErrorMessage))
        {
            ActivateFallback(result.ErrorMessage!);
            return await _fallback.TranslateSingleSegmentAsync(request, cancellationToken);
        }
        return result;
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
        _primary.CheckReadiness(settings, keyStore);

    private void ActivateFallback(string errorMessage)
    {
        _fallbackActive = true;
        const string Note = "CTranslate2 unavailable — using NLLB fallback";
        _log.Warning($"[Translation] CTranslate2 failed ({errorMessage}); switching to NLLB fallback for this session.");
        _onFallback(Note);
    }

    // Detect errors that indicate CTranslate2 or its dependencies are missing,
    // as opposed to a transient runtime or model error.
    private static bool IsCTranslate2InitError(string? msg)
    {
        if (string.IsNullOrEmpty(msg)) return false;
        return msg.Contains("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("ImportError", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("No module named 'ctranslate2'", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("ctranslate2", StringComparison.OrdinalIgnoreCase) && msg.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }
}
