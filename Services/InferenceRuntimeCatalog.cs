using System;
using Babel.Player.Models;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Centralizes stage-specific compute-profile, runtime, and provider normalization so settings
/// migration, registries, and session provenance use one mapping.
/// </summary>
public static class InferenceRuntimeCatalog
{
    public static ComputeProfile MapLegacyRuntimeToProfile(InferenceRuntime runtime) => runtime switch
    {
        InferenceRuntime.Containerized => ComputeProfile.Gpu,
        InferenceRuntime.Cloud => ComputeProfile.Cloud,
        _ => ComputeProfile.Cpu,
    };

    public static InferenceRuntime ResolveRuntime(ComputeProfile profile) => profile switch
    {
        ComputeProfile.Gpu => InferenceRuntime.Containerized,
        ComputeProfile.Cloud => InferenceRuntime.Cloud,
        _ => InferenceRuntime.Local,
    };

    public static ComputeProfile InferTranscriptionProfile(string? providerId) => providerId switch
    {
        ProviderNames.OpenAiWhisperApi
            or ProviderNames.GoogleStt
            or ProviderNames.GeminiTranscription => ComputeProfile.Cloud,
        _ => ComputeProfile.Cpu,
    };

    public static ComputeProfile InferTranslationProfile(string? providerId) => providerId switch
    {
        ProviderNames.Deepl
            or ProviderNames.OpenAi
            or ProviderNames.GoogleTranslateFree
            or ProviderNames.GeminiTranslation => ComputeProfile.Cloud,
        _ => ComputeProfile.Cpu,
    };

    public static ComputeProfile InferTtsProfile(string? providerId) => providerId switch
    {
        ProviderNames.Piper => ComputeProfile.Cpu,
        ProviderNames.XttsContainer => ComputeProfile.Gpu,
        ProviderNames.Qwen => ComputeProfile.Gpu,
        _ => ComputeProfile.Cloud,
    };

    public static string DefaultTranscriptionProvider(ComputeProfile profile) => profile switch
    {
        ComputeProfile.Cloud => ProviderNames.OpenAiWhisperApi,
        _ => ProviderNames.FasterWhisper,
    };

    public static string DefaultTranslationProvider(ComputeProfile profile) => profile switch
    {
        ComputeProfile.Cpu or ComputeProfile.Gpu => ProviderNames.Nllb200,
        _ => ProviderNames.GoogleTranslateFree,
    };

    public static string DefaultTtsProvider(ComputeProfile profile) => profile switch
    {
        ComputeProfile.Cpu => ProviderNames.Piper,
        ComputeProfile.Gpu => ProviderNames.XttsContainer,
        _ => ProviderNames.EdgeTts,
    };

    public static string DefaultTranscriptionProvider(InferenceRuntime runtime) =>
        DefaultTranscriptionProvider(MapLegacyRuntimeToProfile(runtime));

    public static string DefaultTranslationProvider(InferenceRuntime runtime) =>
        DefaultTranslationProvider(MapLegacyRuntimeToProfile(runtime));

    public static string DefaultTtsProvider(InferenceRuntime runtime) =>
        DefaultTtsProvider(MapLegacyRuntimeToProfile(runtime));

    public static string NormalizeTranscriptionProvider(ComputeProfile profile, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return DefaultTranscriptionProvider(profile);

        if (string.Equals(providerId, ProviderNames.ContainerizedService, StringComparison.Ordinal))
            return DefaultTranscriptionProvider(profile);

        if (!IsKnownTranscriptionProvider(providerId))
            return providerId;

        if (profile == ComputeProfile.Cloud)
        {
            return providerId switch
            {
                ProviderNames.GoogleStt => ProviderNames.GoogleStt,
                ProviderNames.GeminiTranscription => ProviderNames.GeminiTranscription,
                ProviderNames.OpenAiWhisperApi => ProviderNames.OpenAiWhisperApi,
                _ => DefaultTranscriptionProvider(profile),
            };
        }

        return ProviderNames.FasterWhisper;
    }

    public static string NormalizeTranslationProvider(ComputeProfile profile, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return DefaultTranslationProvider(profile);

        if (string.Equals(providerId, ProviderNames.ContainerizedService, StringComparison.Ordinal))
            return DefaultTranslationProvider(profile);

        if (!IsKnownTranslationProvider(providerId))
            return providerId;

        if (profile == ComputeProfile.Cpu)
        {
            return providerId switch
            {
                ProviderNames.CTranslate2 => ProviderNames.CTranslate2,
                ProviderNames.Nllb200 => ProviderNames.Nllb200,
                _ => DefaultTranslationProvider(profile),
            };
        }

        if (profile == ComputeProfile.Gpu)
            return ProviderNames.Nllb200;

        return providerId switch
        {
            ProviderNames.Deepl => ProviderNames.Deepl,
            ProviderNames.OpenAi => ProviderNames.OpenAi,
            ProviderNames.GeminiTranslation => ProviderNames.GeminiTranslation,
            ProviderNames.GoogleTranslateFree => ProviderNames.GoogleTranslateFree,
            _ => DefaultTranslationProvider(profile),
        };
    }

    public static string NormalizeTtsProvider(ComputeProfile profile, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return DefaultTtsProvider(profile);

        if (string.Equals(providerId, ProviderNames.ContainerizedService, StringComparison.Ordinal))
            return DefaultTtsProvider(profile);

        if (!IsKnownTtsProvider(providerId))
            return providerId;

        return profile switch
        {
            ComputeProfile.Cpu => ProviderNames.Piper,
            ComputeProfile.Gpu => providerId switch
            {
                ProviderNames.Qwen => ProviderNames.Qwen,
                _ => ProviderNames.XttsContainer,
            },
            _ => providerId switch
            {
                ProviderNames.ElevenLabs => ProviderNames.ElevenLabs,
                ProviderNames.GoogleCloudTts => ProviderNames.GoogleCloudTts,
                ProviderNames.OpenAiTts => ProviderNames.OpenAiTts,
                ProviderNames.EdgeTts => ProviderNames.EdgeTts,
                _ => DefaultTtsProvider(profile),
            },
        };
    }

    public static InferenceRuntime InferTranscriptionRuntime(string? providerId) =>
        ResolveRuntime(InferTranscriptionProfile(providerId));

    public static InferenceRuntime InferTranslationRuntime(string? providerId) =>
        ResolveRuntime(InferTranslationProfile(providerId));

    public static InferenceRuntime InferTtsRuntime(string? providerId) =>
        ResolveRuntime(InferTtsProfile(providerId));

    public static string NormalizeTranscriptionProvider(InferenceRuntime runtime, string? providerId) =>
        NormalizeTranscriptionProvider(MapLegacyRuntimeToProfile(runtime), providerId);

    public static string NormalizeTranslationProvider(InferenceRuntime runtime, string? providerId) =>
        NormalizeTranslationProvider(MapLegacyRuntimeToProfile(runtime), providerId);

    public static string NormalizeTtsProvider(InferenceRuntime runtime, string? providerId) =>
        NormalizeTtsProvider(MapLegacyRuntimeToProfile(runtime), providerId);

    public static void NormalizeSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.TranscriptionProfile = ResolveConfiguredProfile(
            settings.TranscriptionProfile,
            settings.TranscriptionProvider,
            InferTranscriptionProfile);
        settings.TranslationProfile = ResolveConfiguredProfile(
            settings.TranslationProfile,
            settings.TranslationProvider,
            InferTranslationProfile);
        settings.TtsProfile = ResolveConfiguredProfile(
            settings.TtsProfile,
            settings.TtsProvider,
            InferTtsProfile);

        settings.TranscriptionProvider = NormalizeTranscriptionProvider(
            settings.TranscriptionProfile,
            settings.TranscriptionProvider);
        settings.TranslationProvider = NormalizeTranslationProvider(
            settings.TranslationProfile,
            settings.TranslationProvider);
        settings.TtsProvider = NormalizeTtsProvider(
            settings.TtsProfile,
            settings.TtsProvider);
    }

    private static ComputeProfile ResolveConfiguredProfile(
        ComputeProfile configuredProfile,
        string? providerId,
        Func<string?, ComputeProfile> inferProfile)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return configuredProfile;

        return providerId switch
        {
            ProviderNames.ContainerizedService => ComputeProfile.Gpu,
            _ => configuredProfile,
        };
    }

    public static bool IsKnownTranscriptionProvider(string? providerId) => providerId switch
    {
        ProviderNames.FasterWhisper
            or ProviderNames.OpenAiWhisperApi
            or ProviderNames.GoogleStt
            or ProviderNames.GeminiTranscription => true,
        _ => false,
    };

    public static bool IsKnownTranslationProvider(string? providerId) => providerId switch
    {
        ProviderNames.Nllb200
            or ProviderNames.CTranslate2
            or ProviderNames.GoogleTranslateFree
            or ProviderNames.Deepl
            or ProviderNames.OpenAi
            or ProviderNames.GeminiTranslation => true,
        _ => false,
    };

    public static bool IsKnownTtsProvider(string? providerId) => providerId switch
    {
        ProviderNames.Piper
            or ProviderNames.EdgeTts
            or ProviderNames.ElevenLabs
            or ProviderNames.GoogleCloudTts
            or ProviderNames.OpenAiTts
            or ProviderNames.XttsContainer
            or ProviderNames.Qwen => true,
        _ => false,
    };
}
