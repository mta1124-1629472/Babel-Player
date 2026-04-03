using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services.Registries;

public interface ITtsRegistry
{
    IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null);
    IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings);
    ITtsProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null);
    ProviderReadiness CheckReadiness(string providerId, string modelOrVoice, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null);
    Task<bool> EnsureModelAsync(string providerId, string modelOrVoice, AppSettings settings,
                                 IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null);
}

public sealed class TtsRegistry : ITtsRegistry
{
    private readonly AppLog _log;
    private readonly ContainerizedServiceProbe? _containerizedProbe;

    public TtsRegistry(AppLog log, ContainerizedServiceProbe? containerizedProbe = null)
    {
        _log = log;
        _containerizedProbe = containerizedProbe;
    }

    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null)
    {
        if (profile is null)
        {
            return
            [
                new(
                    ProviderNames.Piper,
                    "Piper (Local)",
                    false,
                    null,
                    PiperVoices,
                    SupportedRuntimes: [InferenceRuntime.Local],
                    DefaultRuntime: InferenceRuntime.Local),
                .. GetAvailableProviders(ComputeProfile.Cloud),
            ];
        }

        if (profile == ComputeProfile.Cpu)
        {
            return
            [
                new(
                    ProviderNames.Piper,
                    "Piper (Local)",
                    false,
                    null,
                    PiperVoices,
                    SupportedRuntimes: [InferenceRuntime.Local],
                    DefaultRuntime: InferenceRuntime.Local),
            ];
        }

        if (profile == ComputeProfile.Gpu)
        {
            // GPU TTS remains gated in phase 1. Keep runtime support internal for legacy
            // XTTS configurations, but do not surface it in public picker lists.
            return [];
        }

        var providers = new List<ProviderDescriptor>
        {
            new(
                ProviderNames.EdgeTts,
                "Edge TTS",
                false,
                null,
                EdgeTtsVoices,
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud),
            new(
                ProviderNames.ElevenLabs,
                "ElevenLabs API",
                true,
                CredentialKeys.ElevenLabs,
                ["eleven_multilingual_v2", "eleven_turbo_v2_5", "eleven_flash_v2_5"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud,
                IsImplemented: true),
            new(
                ProviderNames.OpenAiTts,
                "OpenAI API",
                true,
                CredentialKeys.OpenAi,
                ["tts-1", "tts-1-hd", "gpt-4o-mini-tts"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud,
                IsImplemented: true),
        };

        return providers;
    }

    public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings)
    {
        var normalizedProviderId = ResolveProviderId(providerId, settings, profile);
        if (profile == ComputeProfile.Gpu && string.Equals(normalizedProviderId, ProviderNames.XttsContainer, StringComparison.Ordinal))
            return ["xtts-v2"];

        return GetAvailableProviders(profile)
            .FirstOrDefault(p => p.Id == normalizedProviderId)?.SupportedModels
            ?? ["default"];
    }

    public ProviderReadiness CheckReadiness(string providerId, string modelOrVoice, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null)
    {
        var resolvedProfile = ResolveProfile(providerId, settings, profile);
        var resolvedRuntime = InferenceRuntimeCatalog.ResolveRuntime(resolvedProfile);
        var normalizedProviderId = ResolveProviderId(providerId, settings, resolvedProfile);
        var desc = (resolvedProfile == ComputeProfile.Gpu && string.Equals(normalizedProviderId, ProviderNames.XttsContainer, StringComparison.Ordinal))
            ? new ProviderDescriptor(
                ProviderNames.XttsContainer,
                "XTTS v2 (Container)",
                false,
                null,
                ["xtts-v2"],
                SupportedRuntimes: [InferenceRuntime.Containerized],
                DefaultRuntime: InferenceRuntime.Containerized,
                IsImplemented: true)
            : GetAvailableProviders(resolvedProfile).FirstOrDefault(p => p.Id == normalizedProviderId);
        if (desc == null)
            return new ProviderReadiness(false, $"Unknown TTS provider '{normalizedProviderId}'.");
        if (!desc.IsImplemented)
            return new ProviderReadiness(false, $"Provider '{desc.DisplayName}' is not implemented yet.");
        if (desc.RequiresApiKey && string.IsNullOrEmpty(keyStore?.GetKey(desc.CredentialKey!)))
            return new ProviderReadiness(false, $"API key missing for provider '{desc.DisplayName}'.");

        if (resolvedRuntime == InferenceRuntime.Containerized)
            return ContainerizedProviderReadiness.CheckTts(settings, _containerizedProbe);

        var provider = CreateProvider(normalizedProviderId, settings, keyStore, resolvedProfile);
        return provider.CheckReadiness(settings, keyStore);
    }

    public async Task<bool> EnsureModelAsync(string providerId, string modelOrVoice, AppSettings settings,
                                              IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null)
    {
        var resolvedProfile = ResolveProfile(providerId, settings, profile);
        var normalizedProviderId = ResolveProviderId(providerId, settings, resolvedProfile);
        var desc = GetAvailableProviders(resolvedProfile).FirstOrDefault(p => p.Id == normalizedProviderId);
        if (desc == null || !desc.IsImplemented) return false;
        var provider = CreateProvider(normalizedProviderId, settings, keyStore, resolvedProfile);
        return await provider.EnsureReadyAsync(settings, progress, ct);
    }

    public ITtsProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null)
    {
        var resolvedProfile = ResolveProfile(providerId, settings, profile);
        var resolvedRuntime = InferenceRuntimeCatalog.ResolveRuntime(resolvedProfile);
        var normalizedProviderId = ResolveProviderId(providerId, settings, resolvedProfile);

        if (resolvedRuntime == InferenceRuntime.Containerized)
        {
            return normalizedProviderId switch
            {
                ProviderNames.XttsContainer => new XttsContainerTtsProvider(
                    new ContainerizedInferenceClient(settings.EffectiveContainerizedServiceUrl, _log),
                    _log),
                _ => throw new PipelineProviderException(
                    $"Containerized TTS provider '{providerId}' is not implemented. Select a supported provider.")
            };
        }

        return normalizedProviderId switch
        {
            ProviderNames.Piper => new PiperTtsProvider(_log, settings.PiperModelDir),
            ProviderNames.EdgeTts => new EdgeTtsProvider(_log),
            ProviderNames.ElevenLabs => new ElevenLabsTtsProvider(
                _log, keyStore?.GetKey(CredentialKeys.ElevenLabs) ?? string.Empty),
            ProviderNames.GoogleCloudTts => new GoogleCloudTtsProvider(
                _log, keyStore?.GetKey(CredentialKeys.GoogleAi) ?? string.Empty),
            ProviderNames.OpenAiTts => new OpenAiTtsProvider(
                _log, keyStore?.GetKey(CredentialKeys.OpenAi) ?? string.Empty),
            _ => throw new PipelineProviderException(
                $"TTS provider '{providerId}' is not implemented. " +
                "Select an implemented provider in Settings.")
        };
    }

    private static ComputeProfile ResolveProfile(
        string providerId,
        AppSettings settings,
        ComputeProfile? profile)
    {
        if (profile.HasValue)
            return profile.Value;

        if (string.Equals(settings.TtsProvider, providerId, StringComparison.Ordinal))
            return settings.TtsProfile;

        return InferenceRuntimeCatalog.InferTtsProfile(providerId);
    }

    private static string ResolveProviderId(string providerId, AppSettings settings, ComputeProfile profile)
    {
        if (!InferenceRuntimeCatalog.IsKnownTtsProvider(providerId)
            && !string.Equals(providerId, ProviderNames.ContainerizedService, StringComparison.Ordinal))
        {
            return providerId;
        }

        if (string.Equals(settings.TtsProvider, providerId, StringComparison.Ordinal))
            return InferenceRuntimeCatalog.NormalizeTtsProvider(profile, settings.TtsProvider);

        return InferenceRuntimeCatalog.NormalizeTtsProvider(profile, providerId);
    }
    
    public static readonly IReadOnlyList<string> PiperVoices =
    [
        "en_US-lessac-medium",
        "en_US-ryan-high",
        "en_US-ljspeech-high",
        "en_GB-alan-medium",
        "de_DE-thorsten-medium",
        "fr_FR-gilles-low",
        "es_ES-mls_10246-low",
    ];

    public static readonly IReadOnlyList<string> EdgeTtsVoices =
    [
        "en-US-AriaNeural",    "en-US-GuyNeural",     "en-US-JennyNeural",   "en-US-ChristopherNeural",
        "en-GB-SoniaNeural",   "en-GB-RyanNeural",    "en-AU-NatashaNeural", "en-AU-WilliamNeural",
        "es-ES-ElviraNeural",  "es-ES-AlvaroNeural",  "fr-FR-DeniseNeural",  "fr-FR-HenriNeural",
        "de-DE-KatjaNeural",   "de-DE-ConradNeural",  "it-IT-ElsaNeural",    "it-IT-DiegoNeural",
        "pt-BR-FranciscaNeural","pt-BR-AntonioNeural", "ja-JP-NanamiNeural",  "ja-JP-KeitaNeural",
        "ko-KR-SunHiNeural",   "ko-KR-InJoonNeural",  "zh-CN-XiaoxiaoNeural","zh-CN-YunxiNeural",
        "ar-SA-ZariyahNeural", "ar-SA-HamedNeural",   "hi-IN-SwaraNeural",   "hi-IN-MadhurNeural",
        "ru-RU-SvetlanaNeural","ru-RU-DmitryNeural",
    ];
}
