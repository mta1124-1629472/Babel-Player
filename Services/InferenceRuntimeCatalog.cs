using System;
using Babel.Player.Models;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Centralizes stage-specific runtime/provider normalization so settings migration,
/// registries, and session provenance use one mapping.
/// </summary>
public static class InferenceRuntimeCatalog
{
    public static InferenceRuntime InferTranscriptionRuntime(string? providerId) => providerId switch
    {
        ProviderNames.ContainerizedService => InferenceRuntime.Containerized,
        ProviderNames.OpenAiWhisperApi or ProviderNames.GoogleStt => InferenceRuntime.Cloud,
        _ => InferenceRuntime.Local,
    };

    public static InferenceRuntime InferTranslationRuntime(string? providerId) => providerId switch
    {
        ProviderNames.ContainerizedService => InferenceRuntime.Containerized,
        ProviderNames.Nllb200 => InferenceRuntime.Local,
        _ => InferenceRuntime.Cloud,
    };

    public static InferenceRuntime InferTtsRuntime(string? providerId) => providerId switch
    {
        ProviderNames.ContainerizedService => InferenceRuntime.Containerized,
        ProviderNames.Piper => InferenceRuntime.Local,
        _ => InferenceRuntime.Cloud,
    };

    public static string DefaultTranscriptionProvider(InferenceRuntime runtime) => runtime switch
    {
        InferenceRuntime.Containerized => ProviderNames.FasterWhisper,
        InferenceRuntime.Cloud => ProviderNames.OpenAiWhisperApi,
        _ => ProviderNames.FasterWhisper,
    };

    public static string DefaultTranslationProvider(InferenceRuntime runtime) => runtime switch
    {
        InferenceRuntime.Containerized => ProviderNames.GoogleTranslateFree,
        InferenceRuntime.Local => ProviderNames.Nllb200,
        _ => ProviderNames.GoogleTranslateFree,
    };

    public static string DefaultTtsProvider(InferenceRuntime runtime) => runtime switch
    {
        InferenceRuntime.Containerized => ProviderNames.EdgeTts,
        InferenceRuntime.Local => ProviderNames.Piper,
        _ => ProviderNames.EdgeTts,
    };

    public static string NormalizeTranscriptionProvider(InferenceRuntime runtime, string? providerId)
    {
        if (runtime == InferenceRuntime.Containerized)
            return ProviderNames.FasterWhisper;

        if (runtime == InferenceRuntime.Cloud)
        {
            return providerId switch
            {
                ProviderNames.GoogleStt => ProviderNames.GoogleStt,
                ProviderNames.OpenAiWhisperApi => ProviderNames.OpenAiWhisperApi,
                _ => DefaultTranscriptionProvider(runtime),
            };
        }

        return ProviderNames.FasterWhisper;
    }

    public static string NormalizeTranslationProvider(InferenceRuntime runtime, string? providerId)
    {
        if (runtime == InferenceRuntime.Containerized)
            return ProviderNames.GoogleTranslateFree;

        if (runtime == InferenceRuntime.Local)
            return ProviderNames.Nllb200;

        return providerId switch
        {
            ProviderNames.Deepl => ProviderNames.Deepl,
            ProviderNames.OpenAi => ProviderNames.OpenAi,
            ProviderNames.GoogleTranslateFree => ProviderNames.GoogleTranslateFree,
            _ => DefaultTranslationProvider(runtime),
        };
    }

    public static string NormalizeTtsProvider(InferenceRuntime runtime, string? providerId)
    {
        if (runtime == InferenceRuntime.Containerized)
            return ProviderNames.EdgeTts;

        if (runtime == InferenceRuntime.Local)
            return ProviderNames.Piper;

        return providerId switch
        {
            ProviderNames.ElevenLabs => ProviderNames.ElevenLabs,
            ProviderNames.GoogleCloudTts => ProviderNames.GoogleCloudTts,
            ProviderNames.OpenAiTts => ProviderNames.OpenAiTts,
            ProviderNames.EdgeTts => ProviderNames.EdgeTts,
            _ => DefaultTtsProvider(runtime),
        };
    }

    public static void NormalizeSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.TranscriptionRuntime = ResolveConfiguredRuntime(
            settings.TranscriptionRuntime,
            settings.TranscriptionProvider,
            InferTranscriptionRuntime);
        settings.TranslationRuntime = ResolveConfiguredRuntime(
            settings.TranslationRuntime,
            settings.TranslationProvider,
            InferTranslationRuntime);
        settings.TtsRuntime = ResolveConfiguredRuntime(
            settings.TtsRuntime,
            settings.TtsProvider,
            InferTtsRuntime);

        settings.TranscriptionProvider = NormalizeTranscriptionProvider(
            settings.TranscriptionRuntime,
            settings.TranscriptionProvider);
        settings.TranslationProvider = NormalizeTranslationProvider(
            settings.TranslationRuntime,
            settings.TranslationProvider);
        settings.TtsProvider = NormalizeTtsProvider(
            settings.TtsRuntime,
            settings.TtsProvider);
    }

    private static InferenceRuntime ResolveConfiguredRuntime(
        InferenceRuntime configuredRuntime,
        string? providerId,
        Func<string?, InferenceRuntime> inferRuntime)
    {
        if (string.Equals(providerId, ProviderNames.ContainerizedService, StringComparison.Ordinal))
            return InferenceRuntime.Containerized;

        return providerId switch
        {
            null or "" => configuredRuntime,
            _ => configuredRuntime == default && inferRuntime(providerId) != InferenceRuntime.Local
                ? inferRuntime(providerId)
                : configuredRuntime,
        };
    }
}
