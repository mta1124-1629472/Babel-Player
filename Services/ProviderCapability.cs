using System.Collections.Generic;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;

namespace Babel.Player.Services;

/// <summary>
/// Validates provider and model selections before a pipeline stage runs.
/// Throws <see cref="PipelineProviderException"/> with an explicit, human-readable
/// message when the selection cannot be executed.
///
/// Contract:
///   - Supported providers return cleanly.
///   - Unsupported providers throw — no silent fallback to another provider.
///   - Providers that require an API key surface a specific key-missing message.
///   - Invalid model names for a supported provider throw with the valid list.
/// </summary>
public static class ProviderCapability
{
    private static readonly HashSet<string> _transcriptionSupported =
        [ProviderNames.FasterWhisper];
    private static readonly HashSet<string> _translationSupported =
        [ProviderNames.GoogleTranslateFree, ProviderNames.Nllb200];
    private static readonly HashSet<string> _ttsSupported =
        [ProviderNames.EdgeTts, ProviderNames.Piper];

    private static readonly HashSet<string> _fasterWhisperModels =
        ["tiny", "base", "small", "medium", "large-v3"];

    private static readonly HashSet<string> _nllbModels =
        ["nllb-200-distilled-600M", "nllb-200-distilled-1.3B", "nllb-200-1.3B"];

    // ---------------------------------------------------------------------------
    // Transcription
    // ---------------------------------------------------------------------------

    public static void ValidateTranscription(string provider, string model, ApiKeyStore? keys)
    {
        if (!_transcriptionSupported.Contains(provider))
        {
            var credKey = CredentialKeyFor(provider);
            if (credKey is not null && keys is not null && !keys.HasKey(credKey))
            {
                throw new PipelineProviderException(
                    $"Transcription provider '{provider}' is not implemented yet. " +
                    $"Only '{ProviderNames.FasterWhisper}' is currently supported. " +
                    $"An API key for '{credKey}' would also be required.");
            }

            throw new PipelineProviderException(
                $"Transcription provider '{provider}' is not implemented. " +
                $"Only '{ProviderNames.FasterWhisper}' is currently supported.");
        }

        if (provider == ProviderNames.FasterWhisper && !_fasterWhisperModels.Contains(model))
        {
            throw new PipelineProviderException(
                $"Model '{model}' is not valid for provider '{ProviderNames.FasterWhisper}'. " +
                $"Valid models: tiny, base, small, medium, large-v3.");
        }
    }

    // ---------------------------------------------------------------------------
    // Translation
    // ---------------------------------------------------------------------------

    public static void ValidateTranslation(string provider, string model, ApiKeyStore? keys)
    {
        if (_translationSupported.Contains(provider))
        {
            if (provider == ProviderNames.Nllb200 && !_nllbModels.Contains(model))
                throw new PipelineProviderException(
                    $"Model '{model}' is not valid for provider '{ProviderNames.Nllb200}'. " +
                    $"Valid models: nllb-200-distilled-600M, nllb-200-distilled-1.3B, nllb-200-1.3B.");
            return;
        }

        var credKey = CredentialKeyFor(provider);
        if (credKey is not null && keys is not null && !keys.HasKey(credKey))
        {
            throw new PipelineProviderException(
                $"Translation provider '{provider}' is not implemented yet. " +
                $"Only '{ProviderNames.GoogleTranslateFree}' is currently supported. " +
                $"An API key for '{credKey}' would also be required.");
        }

        throw new PipelineProviderException(
            $"Translation provider '{provider}' is not implemented. " +
            $"Only '{ProviderNames.GoogleTranslateFree}' is currently supported.");
    }

    // ---------------------------------------------------------------------------
    // TTS
    // ---------------------------------------------------------------------------

    public static void ValidateTts(string provider, string voiceOrModel, ApiKeyStore? keys)
    {
        if (_ttsSupported.Contains(provider))
            return;

        var credKey = CredentialKeyFor(provider);
        if (credKey is not null && keys is not null && !keys.HasKey(credKey))
        {
            throw new PipelineProviderException(
                $"TTS provider '{provider}' is not implemented yet. " +
                $"Only '{ProviderNames.EdgeTts}' is currently supported. " +
                $"An API key for '{credKey}' would also be required.");
        }

        throw new PipelineProviderException(
            $"TTS provider '{provider}' is not implemented. " +
            $"Only '{ProviderNames.EdgeTts}' is currently supported.");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Returns the <see cref="ApiKeyStore"/> credential key for providers that
    /// require an API key, or null for providers that do not.
    /// Mirrors the mapping in <c>Models/ProviderOptions.cs</c>.
    /// </summary>
    private static string? CredentialKeyFor(string provider) => provider switch
    {
        ProviderNames.OpenAiWhisperApi => CredentialKeys.OpenAi,
        ProviderNames.OpenAiTts        => CredentialKeys.OpenAi,
        ProviderNames.OpenAi           => CredentialKeys.OpenAi,
        ProviderNames.GoogleStt        => CredentialKeys.GoogleAi,
        ProviderNames.GoogleCloudTts   => CredentialKeys.GoogleAi,
        ProviderNames.ElevenLabs       => CredentialKeys.ElevenLabs,
        ProviderNames.Deepl            => CredentialKeys.Deepl,
        _                              => null,
    };
}
