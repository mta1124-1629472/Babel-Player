using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services.Registries;

public interface ITranscriptionRegistry
{
    IReadOnlyList<ProviderDescriptor> GetAvailableProviders();
    ITranscriptionProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null);
    ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore);
    Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings,
                                 IProgress<double>? progress = null, CancellationToken ct = default);
}

public sealed class TranscriptionRegistry : ITranscriptionRegistry
{
    private readonly AppLog _log;

    public TranscriptionRegistry(AppLog log)
    {
        _log = log;
    }

    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders() =>
    [
        new ProviderDescriptor(
            ProviderNames.FasterWhisper,
            "Faster Whisper (Local)",
            false,
            null,
            ["tiny", "base", "small", "medium", "large-v3"]),
        new ProviderDescriptor(
            ProviderNames.ContainerizedService,
            "Containerized Inference Service",
            false,
            null,
            ["tiny", "base", "small", "medium", "large-v3"]),
        new ProviderDescriptor(
            ProviderNames.OpenAiWhisperApi,
            "OpenAI Whisper API",
            true,
            CredentialKeys.OpenAi,
            ["whisper-1", "gpt-4o-transcribe"],
            IsImplemented: false),
        new ProviderDescriptor(
            ProviderNames.GoogleStt,
            "Google STT",
            true,
            CredentialKeys.GoogleAi,
            ["default"],
            IsImplemented: false)
    ];

    public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore)
    {
        var desc = GetAvailableProviders().FirstOrDefault(p => p.Id == providerId);
        if (desc == null)
            return new ProviderReadiness(false, $"Unknown transcription provider '{providerId}'.");
        if (!desc.IsImplemented)
            return new ProviderReadiness(false, $"Provider '{desc.DisplayName}' is not implemented yet.");
        if (desc.RequiresApiKey && string.IsNullOrEmpty(keyStore?.GetKey(desc.CredentialKey!)))
            return new ProviderReadiness(false, $"API key missing for provider '{desc.DisplayName}'.");

        var provider = CreateProvider(providerId, settings, keyStore);
        return provider.CheckReadiness(settings, keyStore);
    }

    public async Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings,
                                              IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var desc = GetAvailableProviders().FirstOrDefault(p => p.Id == providerId);
        if (desc == null || !desc.IsImplemented) return false;
        var provider = CreateProvider(providerId, settings);
        return await provider.EnsureReadyAsync(settings, progress, ct);
    }

    public ITranscriptionProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null)
    {
        return providerId switch
        {
            ProviderNames.FasterWhisper => new FasterWhisperTranscriptionProvider(_log),
            ProviderNames.ContainerizedService => new ContainerizedTranscriptionProvider(
                new ContainerizedInferenceClient(settings.ContainerizedServiceUrl, _log), _log),
            _ => throw new PipelineProviderException(
                $"Transcription provider '{providerId}' is not implemented. " +
                "Select an implemented provider in Settings.")
        };
    }
}
