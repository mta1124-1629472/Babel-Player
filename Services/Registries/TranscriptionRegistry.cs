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
    IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null);
    IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings);
    ITranscriptionProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null);
    ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null);
    Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings,
                                 IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null);
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

    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null)
    {
        if (profile is null)
        {
            return
            [
                new(
                    ProviderNames.FasterWhisper,
                    "Faster Whisper",
                    false,
                    null,
                    ["tiny", "base", "small", "medium", "large-v3"],
                    SupportedRuntimes: [InferenceRuntime.Local, InferenceRuntime.Containerized],
                    DefaultRuntime: InferenceRuntime.Local),
                .. GetAvailableProviders(ComputeProfile.Cloud),
            ];
        }

        if (profile is ComputeProfile.Cpu or ComputeProfile.Gpu)
        {
            var runtime = profile == ComputeProfile.Gpu
                ? InferenceRuntime.Containerized
                : InferenceRuntime.Local;
            return
            [
                new(
                    ProviderNames.FasterWhisper,
                    profile == ComputeProfile.Gpu ? "Faster Whisper (Local GPU Host)" : "Faster Whisper",
                    false,
                    null,
                    ["tiny", "base", "small", "medium", "large-v3"],
                    SupportedRuntimes: [runtime],
                    DefaultRuntime: runtime),
            ];
        }

        var providers = new List<ProviderDescriptor>
        {
            new(
                ProviderNames.OpenAiWhisperApi,
                "OpenAI Whisper API",
                true,
                CredentialKeys.OpenAi,
                ["whisper-1", "gpt-4o-transcribe"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud,
                IsImplemented: true),
            new(
                ProviderNames.GoogleStt,
                "Google STT",
                true,
                CredentialKeys.GoogleAi,
                ["default"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud,
                IsImplemented: true),
            new(
                ProviderNames.GeminiTranscription,
                "Google Gemini",
                true,
                CredentialKeys.GoogleGemini,
                ["gemini-2.0-flash", "gemini-2.5-flash-preview-04-17"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud,
                IsImplemented: true,
                Notes: "Supports one-pass transcription + translation via GeminiTranscribeMode.TranscribeAndTranslate. " +
                       "When using TranscribeAndTranslate mode, skip the translation stage in the workflow coordinator."),
        };

        return providers;
    }

    public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings)
    {
        var normalizedProviderId = ResolveProviderId(providerId, settings, profile);
        return GetAvailableProviders(profile)
            .FirstOrDefault(p => p.Id == normalizedProviderId)?.SupportedModels
            ?? ["default"];
    }

    public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null)
    {
        var resolvedProfile = ResolveProfile(providerId, settings, profile);
        var resolvedRuntime = InferenceRuntimeCatalog.ResolveRuntime(resolvedProfile);
        var normalizedProviderId = ResolveProviderId(providerId, settings, resolvedProfile);
        var desc = GetAvailableProviders(resolvedProfile).FirstOrDefault(p => p.Id == normalizedProviderId);
        if (desc == null)
            return new ProviderReadiness(false, $"Unknown transcription provider '{normalizedProviderId}'.");
        if (!desc.IsImplemented)
            return new ProviderReadiness(false, $"Provider '{desc.DisplayName}' is not implemented yet.");
        if (desc.RequiresApiKey && string.IsNullOrEmpty(keyStore?.GetKey(desc.CredentialKey!)))
            return new ProviderReadiness(false, $"API key missing for provider '{desc.DisplayName}'.");

        if (resolvedRuntime == InferenceRuntime.Containerized)
            return ContainerizedProviderReadiness.CheckTranscription(settings, _containerizedProbe);

        var provider = CreateProvider(normalizedProviderId, settings, keyStore, resolvedProfile);
        return provider.CheckReadiness(settings, keyStore);
    }

    public async Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings,
                                              IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null)
    {
        var resolvedProfile = ResolveProfile(providerId, settings, profile);
        var normalizedProviderId = ResolveProviderId(providerId, settings, resolvedProfile);
        var desc = GetAvailableProviders(resolvedProfile).FirstOrDefault(p => p.Id == normalizedProviderId);
        if (desc == null || !desc.IsImplemented) return false;
        var provider = CreateProvider(normalizedProviderId, settings, keyStore, resolvedProfile);
        return await provider.EnsureReadyAsync(settings, progress, ct);
    }

    public ITranscriptionProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null)
    {
        var resolvedProfile = ResolveProfile(providerId, settings, profile);
        var resolvedRuntime = InferenceRuntimeCatalog.ResolveRuntime(resolvedProfile);
        var normalizedProviderId = ResolveProviderId(providerId, settings, resolvedProfile);

        if (resolvedRuntime == InferenceRuntime.Containerized)
        {
            return new ContainerizedTranscriptionProvider(
                new ContainerizedInferenceClient(settings.EffectiveContainerizedServiceUrl, _log),
                _log);
        }

        return normalizedProviderId switch
        {
            ProviderNames.FasterWhisper => new FasterWhisperTranscriptionProvider(_log),
            ProviderNames.OpenAiWhisperApi => new OpenAiWhisperTranscriptionProvider(
                _log,
                keyStore?.GetKey(CredentialKeys.OpenAi) ?? string.Empty),
            ProviderNames.GoogleStt => new GoogleSttTranscriptionProvider(
                _log,
                keyStore?.GetKey(CredentialKeys.GoogleAi) ?? string.Empty),
            ProviderNames.GeminiTranscription => new GeminiTranscriptionProvider(
                _log,
                keyStore?.GetKey(CredentialKeys.GoogleGemini) ?? string.Empty),
            _ => throw new PipelineProviderException(
                $"Transcription provider '{providerId}' is not implemented. " +
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

        if (string.Equals(settings.TranscriptionProvider, providerId, StringComparison.Ordinal))
            return settings.TranscriptionProfile;

        return InferenceRuntimeCatalog.InferTranscriptionProfile(providerId);
    }

    private static string ResolveProviderId(string providerId, AppSettings settings, ComputeProfile profile)
    {
        if (!InferenceRuntimeCatalog.IsKnownTranscriptionProvider(providerId)
            && !string.Equals(providerId, ProviderNames.ContainerizedService, StringComparison.Ordinal))
        {
            return providerId;
        }

        if (string.Equals(settings.TranscriptionProvider, providerId, StringComparison.Ordinal))
            return InferenceRuntimeCatalog.NormalizeTranscriptionProvider(profile, settings.TranscriptionProvider);

        return InferenceRuntimeCatalog.NormalizeTranscriptionProvider(profile, providerId);
    }
}
