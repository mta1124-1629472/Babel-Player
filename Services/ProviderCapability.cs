using System.Collections.Generic;
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
    private static readonly HashSet<string> _transcriptionSupported = ["faster-whisper"];
    private static readonly HashSet<string> _translationSupported   = ["google-translate-free", "nllb-200"];
    private static readonly HashSet<string> _ttsSupported           = ["edge-tts", "piper"];

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
                    $"Only 'faster-whisper' is currently supported. " +
                    $"An API key for '{credKey}' would also be required.");
            }

            throw new PipelineProviderException(
                $"Transcription provider '{provider}' is not implemented. " +
                $"Only 'faster-whisper' is currently supported.");
        }

        if (provider == "faster-whisper" && !_fasterWhisperModels.Contains(model))
        {
            throw new PipelineProviderException(
                $"Model '{model}' is not valid for provider 'faster-whisper'. " +
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
            if (provider == "nllb-200" && !_nllbModels.Contains(model))
                throw new PipelineProviderException(
                    $"Model '{model}' is not valid for provider 'nllb-200'. " +
                    $"Valid models: nllb-200-distilled-600M, nllb-200-distilled-1.3B, nllb-200-1.3B.");
            return;
        }

        var credKey = CredentialKeyFor(provider);
        if (credKey is not null && keys is not null && !keys.HasKey(credKey))
        {
            throw new PipelineProviderException(
                $"Translation provider '{provider}' is not implemented yet. " +
                $"Only 'google-translate-free' is currently supported. " +
                $"An API key for '{credKey}' would also be required.");
        }

        throw new PipelineProviderException(
            $"Translation provider '{provider}' is not implemented. " +
            $"Only 'google-translate-free' is currently supported.");
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
                $"Only 'edge-tts' is currently supported. " +
                $"An API key for '{credKey}' would also be required.");
        }

        throw new PipelineProviderException(
            $"TTS provider '{provider}' is not implemented. " +
            $"Only 'edge-tts' is currently supported.");
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
        "openai-whisper-api" => "openai",
        "openai-tts"         => "openai",
        "openai"             => "openai",
        "google-stt"         => "google-ai",
        "google-cloud-tts"   => "google-ai",
        "elevenlabs"         => "elevenlabs",
        "deepl"              => "deepl",
        _                    => null,
    };
}
