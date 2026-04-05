using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Registries;

namespace Babel.Player.Services.Credentials;

public sealed class ApiKeyValidationService
{
    private readonly ITranscriptionRegistry _transcriptionRegistry;
    private readonly ITranslationRegistry _translationRegistry;
    private readonly ITtsRegistry _ttsRegistry;
    private readonly Func<string, OpenAiApiClient> _openAiClientFactory;
    private readonly Func<string, DeepLApiClient> _deepLClientFactory;
    private readonly Func<string, ElevenLabsApiClient> _elevenLabsClientFactory;
    private readonly Func<string, GoogleApiClient> _googleClientFactory;

    // Shared fallback HttpClient for HF probes — no auth header baked in so each
    // call uses a new request with its own Authorization header.
    private static readonly HttpClient _defaultHfHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    // Instance field allows tests to inject a stub client via the constructor.
    private readonly HttpClient _hfHttpClient;

    private const string HfWhoAmIUrl        = "https://huggingface.co/api/whoami-v2";
    private const string HfPyannoteRepoId   = "pyannote/speaker-diarization-3.1";

    public ApiKeyValidationService(
        ITranscriptionRegistry transcriptionRegistry,
        ITranslationRegistry translationRegistry,
        ITtsRegistry ttsRegistry,
        Func<string, OpenAiApiClient>? openAiClientFactory = null,
        Func<string, DeepLApiClient>? deepLClientFactory = null,
        Func<string, ElevenLabsApiClient>? elevenLabsClientFactory = null,
        Func<string, GoogleApiClient>? googleClientFactory = null,
        HttpClient? hfHttpClient = null)
    {
        _transcriptionRegistry = transcriptionRegistry;
        _translationRegistry = translationRegistry;
        _ttsRegistry = ttsRegistry;
        _openAiClientFactory = openAiClientFactory ?? (apiKey => new OpenAiApiClient(apiKey));
        _deepLClientFactory = deepLClientFactory ?? (apiKey => new DeepLApiClient(apiKey));
        _elevenLabsClientFactory = elevenLabsClientFactory ?? (apiKey => new ElevenLabsApiClient(apiKey));
        _googleClientFactory = googleClientFactory ?? (apiKey => new GoogleApiClient(apiKey));
        _hfHttpClient = hfHttpClient ?? _defaultHfHttpClient;
    }

    public string? GetAvailabilityMessage(string credentialKey)
    {
        var implementedProviders = GetImplementedProviders(credentialKey);
        if (implementedProviders.Count > 0)
            return null;

        // Some credentials have a direct validation probe even before a full provider
        // is wired in the registry. Surface validation for these immediately.
        if (HasDirectValidationProbe(credentialKey))
            return null;

        return "Live validation unavailable until an implemented provider uses this key.";
    }

    public async Task<ApiKeyValidationResult> ValidateAsync(
        string credentialKey,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return ApiKeyValidationResult.Failure("Enter or save a key before validating.");

        // Google has a direct probe independent of provider implementation state.
        if (credentialKey == CredentialKeys.GoogleAi)
            return await ValidateGoogleAsync(apiKey.Trim(), cancellationToken);

        // HuggingFace: strict two-step live validation.
        if (credentialKey == CredentialKeys.HuggingFace)
            return await ValidateHuggingFaceAsync(apiKey.Trim(), cancellationToken);

        var implementedProviders = GetImplementedProviders(credentialKey);
        if (implementedProviders.Count == 0)
            return ApiKeyValidationResult.Unavailable(
                "Live validation unavailable until an implemented provider uses this key.");

        return credentialKey switch
        {
            CredentialKeys.OpenAi     => await ValidateOpenAiAsync(apiKey.Trim(), implementedProviders, cancellationToken),
            CredentialKeys.Deepl      => await ValidateDeepLAsync(apiKey.Trim(), cancellationToken),
            CredentialKeys.ElevenLabs => await ValidateElevenLabsAsync(apiKey.Trim(), cancellationToken),
            _ => ApiKeyValidationResult.Unavailable(
                "Live validation is not implemented for this credential yet."),
        };
    }

    // ── HuggingFace ───────────────────────────────────────────────────────────
    //
    // Strict two-step check:
    //   1. whoami-v2  — token is live and not revoked
    //   2. model repo probe — token has read access to the gated pyannote model
    //
    // Both must succeed to return Success. This catches:
    //   - Expired / revoked tokens
    //   - Valid token but HF model terms not accepted
    //   - Valid token but token scope too narrow (e.g. write-only org token)

    private async Task<ApiKeyValidationResult> ValidateHuggingFaceAsync(
        string token,
        CancellationToken cancellationToken)
    {
        // Fast format pre-check before making any network calls.
        if (!token.StartsWith("hf_", StringComparison.Ordinal) || token.Length < 30)
            return ApiKeyValidationResult.Failure(
                "Token does not look like a valid HuggingFace user access token " +
                "(expected \"hf_\" prefix, ≥30 characters). " +
                "Get yours at https://huggingface.co/settings/tokens.");

        // Step 1: verify the token is accepted by HF at all.
        using var whoamiReq = new HttpRequestMessage(HttpMethod.Get, HfWhoAmIUrl);
        whoamiReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage whoamiResp;
        try
        {
            whoamiResp = await _hfHttpClient.SendAsync(
                whoamiReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ApiKeyValidationResult.Failure($"HuggingFace whoami request failed: {ex.Message}");
        }

        using (whoamiResp)
        {
            if (whoamiResp.StatusCode == HttpStatusCode.Unauthorized)
                return ApiKeyValidationResult.Failure(
                    "HuggingFace rejected the token (401 Unauthorized). " +
                    "Check that the token is correct and has not been revoked.");

            if (!whoamiResp.IsSuccessStatusCode)
                return ApiKeyValidationResult.Failure(
                    $"HuggingFace whoami returned an unexpected status: " +
                    $"{(int)whoamiResp.StatusCode} {whoamiResp.ReasonPhrase}.");
        }

        // Step 2: verify access to the specific gated pyannote model.
        var repoMetaUrl = $"https://huggingface.co/api/models/{HfPyannoteRepoId}";
        using var repoReq = new HttpRequestMessage(HttpMethod.Get, repoMetaUrl);
        repoReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage repoResp;
        try
        {
            repoResp = await _hfHttpClient.SendAsync(
                repoReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ApiKeyValidationResult.Failure(
                $"pyannote model probe request failed: {ex.Message}");
        }

        using (repoResp)
        {
            if (repoResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return ApiKeyValidationResult.Failure(
                    "Token is valid but cannot access the required pyannote diarization model. " +
                    "Accept the model license on HuggingFace and ensure the token has 'read' scope: " +
                    $"https://huggingface.co/{HfPyannoteRepoId}");

            if (!repoResp.IsSuccessStatusCode)
                return ApiKeyValidationResult.Failure(
                    $"pyannote model probe returned an unexpected status: " +
                    $"{(int)repoResp.StatusCode} {repoResp.ReasonPhrase}.");
        }

        return ApiKeyValidationResult.Success(
            $"Token is valid and has read access to {HfPyannoteRepoId}. Ready for diarization.");
    }

    // ── ElevenLabs ────────────────────────────────────────────────────────────

    private async Task<ApiKeyValidationResult> ValidateElevenLabsAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _elevenLabsClientFactory(apiKey);
            var subscription = await client.GetSubscriptionAsync(cancellationToken);
            var tier = string.IsNullOrEmpty(subscription.Tier) ? "unknown" : subscription.Tier;

            return ApiKeyValidationResult.Success(
                $"Validated for ElevenLabs ({tier} tier). " +
                $"Usage: {subscription.CharacterCount} / {subscription.CharacterLimit} characters.");
        }
        catch (ElevenLabsApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ApiKeyValidationResult.Failure($"ElevenLabs rejected the key: {ex.Message}");
        }
        catch (ElevenLabsApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return ApiKeyValidationResult.Failure($"ElevenLabs rate-limited validation: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiKeyValidationResult.Failure($"Validation failed: {ex.Message}");
        }
    }

    // ── DeepL ─────────────────────────────────────────────────────────────────

    private async Task<ApiKeyValidationResult> ValidateDeepLAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _deepLClientFactory(apiKey);
            var usage = await client.GetUsageAsync(cancellationToken);

            return ApiKeyValidationResult.Success(
                $"Validated for DeepL. Usage: {usage.CharacterCount} / {usage.CharacterLimit} characters.");
        }
        catch (DeepLApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ApiKeyValidationResult.Failure($"DeepL rejected the key: {ex.Message}");
        }
        catch (DeepLApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return ApiKeyValidationResult.Failure($"DeepL rate-limited validation: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiKeyValidationResult.Failure($"Validation failed: {ex.Message}");
        }
    }

    // ── OpenAI ────────────────────────────────────────────────────────────────

    private async Task<ApiKeyValidationResult> ValidateOpenAiAsync(
        string apiKey,
        IReadOnlyList<ProviderDescriptor> implementedProviders,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _openAiClientFactory(apiKey);
            var availableModels = await client.ListModelsAsync(cancellationToken);
            var supportedModels = implementedProviders
                .SelectMany(provider => provider.SupportedModels)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var matchedModels = availableModels
                .Where(model => supportedModels.Contains(model, StringComparer.Ordinal))
                .OrderBy(model => model, StringComparer.Ordinal)
                .ToArray();

            if (matchedModels.Length == 0)
            {
                return ApiKeyValidationResult.Failure(
                    "OpenAI accepted the key, but none of Babel Player's supported OpenAI models are available on this account.");
            }

            return ApiKeyValidationResult.Success(
                $"Validated for OpenAI. Available Babel Player models: {string.Join(", ", matchedModels)}.");
        }
        catch (OpenAiApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ApiKeyValidationResult.Failure($"OpenAI rejected the key: {ex.Message}");
        }
        catch (OpenAiApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return ApiKeyValidationResult.Failure($"OpenAI rate-limited validation: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiKeyValidationResult.Failure($"Validation failed: {ex.Message}");
        }
    }

    // ── Google ────────────────────────────────────────────────────────────────

    private async Task<ApiKeyValidationResult> ValidateGoogleAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _googleClientFactory(apiKey);
            var voices = await client.ListVoicesAsync(cancellationToken);

            return ApiKeyValidationResult.Success(
                $"Google Cloud API key accepted. Cloud TTS voices available: {voices.Count}. " +
                "Note: Google STT and Cloud TTS providers are not yet implemented \u2014 " +
                "your key is ready for when they ship.");
        }
        catch (GoogleApiException ex) when (
            ex.StatusCode is HttpStatusCode.BadRequest
                          or HttpStatusCode.Unauthorized
                          or HttpStatusCode.Forbidden)
        {
            return ApiKeyValidationResult.Failure($"Google rejected the key: {ex.Message}");
        }
        catch (GoogleApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return ApiKeyValidationResult.Failure($"Google rate-limited validation: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiKeyValidationResult.Failure($"Validation failed: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IReadOnlyList<ProviderDescriptor> GetImplementedProviders(string credentialKey)
    {
        return
        [
            .. _transcriptionRegistry.GetAvailableProviders().Where(MatchesCredentialKey),
            .. _translationRegistry.GetAvailableProviders().Where(MatchesCredentialKey),
            .. _ttsRegistry.GetAvailableProviders().Where(MatchesCredentialKey),
        ];

        bool MatchesCredentialKey(ProviderDescriptor provider) =>
            provider.IsImplemented
            && provider.RequiresApiKey
            && string.Equals(provider.CredentialKey, credentialKey, StringComparison.Ordinal);
    }

    // Credentials that have a dedicated live probe independent of any fully
    // implemented provider. Add entries here when validation is wired but the
    // full provider implementation is still pending.
    private static bool HasDirectValidationProbe(string credentialKey) =>
        credentialKey is CredentialKeys.GoogleAi or CredentialKeys.HuggingFace;
}

public sealed record ApiKeyValidationResult(
    bool IsAvailable,
    bool IsValid,
    string Message)
{
    public static ApiKeyValidationResult Success(string message) => new(true, true, message);

    public static ApiKeyValidationResult Failure(string message) => new(true, false, message);

    public static ApiKeyValidationResult Unavailable(string message) => new(false, false, message);
}
