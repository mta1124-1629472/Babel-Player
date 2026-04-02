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
    IReadOnlyList<ProviderDescriptor> GetAvailableProviders(InferenceRuntime? runtime = null);
    ITranscriptionProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, InferenceRuntime? runtime = null);
    ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, InferenceRuntime? runtime = null);
    Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings,
                                 IProgress<double>? progress = null, CancellationToken ct = default, InferenceRuntime? runtime = null);
}

public sealed class TranscriptionRegistry : ITranscriptionRegistry
{
    private readonly AppLog _log;
    private readonly ContainerizedServiceProbe? _containerizedProbe;

    public TranscriptionRegistry(AppLog log, ContainerizedServiceProbe? containerizedProbe = null)
    {
        _log = log;
        _containerizedProbe = containerizedProbe;
    }

    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(InferenceRuntime? runtime = null)
    {
        var providers = new List<ProviderDescriptor>
        {
            new(
                ProviderNames.FasterWhisper,
                "Faster Whisper",
                false,
                null,
                ["tiny", "base", "small", "medium", "large-v3"],
                SupportedRuntimes: [InferenceRuntime.Local, InferenceRuntime.Containerized],
                DefaultRuntime: InferenceRuntime.Local),
            new(
                ProviderNames.OpenAiWhisperApi,
                "OpenAI Whisper API",
                true,
                CredentialKeys.OpenAi,
                ["whisper-1", "gpt-4o-transcribe"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud,
                IsImplemented: false),
            new(
                ProviderNames.GoogleStt,
                "Google STT",
                true,
                CredentialKeys.GoogleAi,
                ["default"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud,
                IsImplemented: false),
        };

        return runtime is null
            ? providers
            : [.. providers.Where(p => p.EffectiveSupportedRuntimes.Contains(runtime.Value))];
    }

    public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, InferenceRuntime? runtime = null)
    {
        var resolvedRuntime = ResolveRuntime(providerId, settings, runtime);
        var normalizedProviderId = InferenceRuntimeCatalog.NormalizeTranscriptionProvider(resolvedRuntime, providerId);
        var desc = GetAvailableProviders(resolvedRuntime).FirstOrDefault(p => p.Id == normalizedProviderId);
        if (desc == null)
            return new ProviderReadiness(false, $"Unknown transcription provider '{normalizedProviderId}'.");
        if (!desc.IsImplemented)
            return new ProviderReadiness(false, $"Provider '{desc.DisplayName}' is not implemented yet.");
        if (desc.RequiresApiKey && string.IsNullOrEmpty(keyStore?.GetKey(desc.CredentialKey!)))
            return new ProviderReadiness(false, $"API key missing for provider '{desc.DisplayName}'.");

        if (resolvedRuntime == InferenceRuntime.Containerized)
            return ContainerizedProviderReadiness.CheckTranscription(settings, _containerizedProbe);

        var provider = CreateProvider(normalizedProviderId, settings, keyStore, resolvedRuntime);
        return provider.CheckReadiness(settings, keyStore);
    }

    public async Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings,
                                              IProgress<double>? progress = null, CancellationToken ct = default, InferenceRuntime? runtime = null)
    {
        var resolvedRuntime = ResolveRuntime(providerId, settings, runtime);
        var normalizedProviderId = InferenceRuntimeCatalog.NormalizeTranscriptionProvider(resolvedRuntime, providerId);
        var desc = GetAvailableProviders(resolvedRuntime).FirstOrDefault(p => p.Id == normalizedProviderId);
        if (desc == null || !desc.IsImplemented) return false;
        var provider = CreateProvider(normalizedProviderId, settings, null, resolvedRuntime);
        return await provider.EnsureReadyAsync(settings, progress, ct);
    }

    public ITranscriptionProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, InferenceRuntime? runtime = null)
    {
        var resolvedRuntime = ResolveRuntime(providerId, settings, runtime);
        var normalizedProviderId = InferenceRuntimeCatalog.NormalizeTranscriptionProvider(resolvedRuntime, providerId);

        if (resolvedRuntime == InferenceRuntime.Containerized)
        {
            return new ContainerizedTranscriptionProvider(
                new ContainerizedInferenceClient(settings.EffectiveContainerizedServiceUrl, _log),
                _log);
        }

        return normalizedProviderId switch
        {
            ProviderNames.FasterWhisper => new FasterWhisperTranscriptionProvider(_log),
            _ => throw new PipelineProviderException(
                $"Transcription provider '{providerId}' is not implemented. " +
                "Select an implemented provider in Settings.")
        };
    }

    private static InferenceRuntime ResolveRuntime(
        string providerId,
        AppSettings settings,
        InferenceRuntime? runtime)
    {
        if (runtime.HasValue)
            return runtime.Value;

        if (string.Equals(settings.TranscriptionProvider, providerId, StringComparison.Ordinal))
            return settings.TranscriptionRuntime;

        return InferenceRuntimeCatalog.InferTranscriptionRuntime(providerId);
    }
}
