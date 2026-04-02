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
    IReadOnlyList<ProviderDescriptor> GetAvailableProviders();
    ITtsProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null);
    ProviderReadiness CheckReadiness(string providerId, string modelOrVoice, AppSettings settings, ApiKeyStore? keyStore);
    Task<bool> EnsureModelAsync(string providerId, string modelOrVoice, AppSettings settings,
                                 IProgress<double>? progress = null, CancellationToken ct = default);
}

public sealed class TtsRegistry : ITtsRegistry
{
    private readonly AppLog _log;

    public TtsRegistry(AppLog log)
    {
        _log = log;
    }

    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders() =>
    [
        new ProviderDescriptor(
            ProviderNames.EdgeTts,
            "Edge TTS (Cloud)",
            false,
            null,
            EdgeTtsVoices),
        new ProviderDescriptor(
            ProviderNames.Piper,
            "Piper (Local)",
            false,
            null,
            PiperVoices),
        new ProviderDescriptor(
            ProviderNames.ContainerizedService,
            "Containerized Inference Service",
            false,
            null,
            EdgeTtsVoices),   // containerized service uses edge-tts voices
        new ProviderDescriptor(
            ProviderNames.ElevenLabs,
            "ElevenLabs API",
            true,
            CredentialKeys.ElevenLabs,
            ["eleven_multilingual_v2", "eleven_turbo_v2_5", "eleven_flash_v2_5"],
            IsImplemented: false),
        new ProviderDescriptor(
            ProviderNames.GoogleCloudTts,
            "Google Cloud TTS",
            true,
            CredentialKeys.GoogleAi,
            ["standard", "wavenet", "neural2"],
            IsImplemented: false),
        new ProviderDescriptor(
            ProviderNames.OpenAiTts,
            "OpenAI API",
            true,
            CredentialKeys.OpenAi,
            ["tts-1", "tts-1-hd", "gpt-4o-mini-tts"],
            IsImplemented: false)
    ];

    public ProviderReadiness CheckReadiness(string providerId, string modelOrVoice, AppSettings settings, ApiKeyStore? keyStore)
    {
        var desc = GetAvailableProviders().FirstOrDefault(p => p.Id == providerId);
        if (desc == null)
            return new ProviderReadiness(false, $"Unknown TTS provider '{providerId}'.");
        if (!desc.IsImplemented)
            return new ProviderReadiness(false, $"Provider '{desc.DisplayName}' is not implemented yet.");
        if (desc.RequiresApiKey && string.IsNullOrEmpty(keyStore?.GetKey(desc.CredentialKey!)))
            return new ProviderReadiness(false, $"API key missing for provider '{desc.DisplayName}'.");

        var provider = CreateProvider(providerId, settings, keyStore);
        return provider.CheckReadiness(settings, keyStore);
    }

    public async Task<bool> EnsureModelAsync(string providerId, string modelOrVoice, AppSettings settings,
                                              IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var desc = GetAvailableProviders().FirstOrDefault(p => p.Id == providerId);
        if (desc == null || !desc.IsImplemented) return false;
        var provider = CreateProvider(providerId, settings);
        return await provider.EnsureReadyAsync(settings, progress, ct);
    }

    public ITtsProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null)
    {
        return providerId switch
        {
            ProviderNames.Piper => new PiperTtsProvider(_log, settings.PiperModelDir),
            ProviderNames.EdgeTts => new EdgeTtsProvider(_log),
            ProviderNames.ContainerizedService => new ContainerizedTtsProvider(
                new ContainerizedInferenceClient(settings.EffectiveContainerizedServiceUrl, _log), _log),
            _ => throw new PipelineProviderException(
                $"TTS provider '{providerId}' is not implemented. " +
                "Select an implemented provider in Settings.")
        };
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
