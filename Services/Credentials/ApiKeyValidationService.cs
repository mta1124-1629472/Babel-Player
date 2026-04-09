using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;

namespace Babel.Player.Services.Credentials;

public sealed class ApiKeyValidationService(
    ITranscriptionRegistry transcriptionRegistry,
    ITranslationRegistry translationRegistry,
    ITtsRegistry ttsRegistry,
    Func<string, OpenAiApiClient>? openAiClientFactory = null,
    Func<string, DeepLApiClient>? deepLClientFactory = null,
    Func<string, ElevenLabsApiClient>? elevenLabsClientFactory = null,
    Func<string, GoogleApiClient>? googleClientFactory = null)
{
    private readonly ITranscriptionRegistry _transcriptionRegistry = transcriptionRegistry;
    private readonly ITranslationRegistry _translationRegistry = translationRegistry;
    private readonly ITtsRegistry _ttsRegistry = ttsRegistry;
    private readonly Func<string, OpenAiApiClient> _openAiClientFactory = openAiClientFactory ?? (apiKey => new OpenAiApiClient(apiKey));
    private readonly Func<string, DeepLApiClient> _deepLClientFactory = deepLClientFactory ?? (apiKey => new DeepLApiClient(apiKey));
    private readonly Func<string, ElevenLabsApiClient> _elevenLabsClientFactory = elevenLabsClientFactory ?? (apiKey => new ElevenLabsApiClient(apiKey));
    private readonly Func<string, GoogleApiClient> _googleClientFactory = googleClientFactory ?? (apiKey => new GoogleApiClient(apiKey));

    /// <summary>
    /// Returns an explanatory message when live validation is not available for the specified credential key.
    /// </summary>
    /// <param name="credentialKey">The credential identifier to check (for example, a value from <c>CredentialKeys</c>).</param>
    /// <returns><c>null</c> if live validation is available for the credential key; otherwise a user-facing message explaining that live validation is unavailable.</returns>
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

    /// <summary>
    /// Validates the given API key for the specified credential provider.
    /// </summary>
    /// <param name="credentialKey">The credential identifier (e.g., CredentialKeys.OpenAi, CredentialKeys.GoogleAi, CredentialKeys.Deepl, CredentialKeys.ElevenLabs) to validate against.</param>
    /// <param name="apiKey">The API key to validate; may be null or whitespace.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// An <see cref="ApiKeyValidationResult"/> indicating whether live validation is available for the credential and whether the provided key is valid.
    /// - Returns a failure result prompting to enter a key if <paramref name="apiKey"/> is null or whitespace.
    /// - Returns an unavailable result when no implemented provider requires the credential or when live validation is not implemented for the credential.
    /// - Returns a success or failure result with a diagnostic message reflecting the provider-specific validation outcome.
    /// </returns>
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

    /// <summary>
    /// Validates an ElevenLabs API key by retrieving the account subscription and usage.
    /// </summary>
    /// <returns>`ApiKeyValidationResult` with `IsAvailable = true` and `IsValid = true` when the key is accepted; the success message includes the subscription tier and character usage/limit. Returns a failure result with a message indicating rejection, rate-limiting, or a general validation failure otherwise.</returns>

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
    /// <summary>
        /// Determines whether the given credential key has a direct live validation probe available.
        /// </summary>
        /// <param name="credentialKey">The credential key to check.</param>
        /// <returns>`true` if the credential key supports direct validation; `false` otherwise.</returns>
    private static bool HasDirectValidationProbe(string credentialKey) =>
        credentialKey == CredentialKeys.GoogleAi;
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
