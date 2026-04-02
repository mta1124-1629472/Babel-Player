using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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

    public ApiKeyValidationService(
        ITranscriptionRegistry transcriptionRegistry,
        ITranslationRegistry translationRegistry,
        ITtsRegistry ttsRegistry,
        Func<string, OpenAiApiClient>? openAiClientFactory = null,
        Func<string, DeepLApiClient>? deepLClientFactory = null,
        Func<string, ElevenLabsApiClient>? elevenLabsClientFactory = null)
    {
        _transcriptionRegistry = transcriptionRegistry;
        _translationRegistry = translationRegistry;
        _ttsRegistry = ttsRegistry;
        _openAiClientFactory = openAiClientFactory ?? (apiKey => new OpenAiApiClient(apiKey));
        _deepLClientFactory = deepLClientFactory ?? (apiKey => new DeepLApiClient(apiKey));
        _elevenLabsClientFactory = elevenLabsClientFactory ?? (apiKey => new ElevenLabsApiClient(apiKey));
    }

    public string? GetAvailabilityMessage(string credentialKey)
    {
        var implementedProviders = GetImplementedProviders(credentialKey);
        if (implementedProviders.Count > 0)
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

        var implementedProviders = GetImplementedProviders(credentialKey);
        if (implementedProviders.Count == 0)
            return ApiKeyValidationResult.Unavailable(
                "Live validation unavailable until an implemented provider uses this key.");

        return credentialKey switch
        {
            CredentialKeys.OpenAi => await ValidateOpenAiAsync(apiKey.Trim(), implementedProviders, cancellationToken),
            CredentialKeys.Deepl => await ValidateDeepLAsync(apiKey.Trim(), cancellationToken),
            CredentialKeys.ElevenLabs => await ValidateElevenLabsAsync(apiKey.Trim(), cancellationToken),
            _ => ApiKeyValidationResult.Unavailable(
                "Live validation is not implemented for this credential yet."),
        };
    }

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