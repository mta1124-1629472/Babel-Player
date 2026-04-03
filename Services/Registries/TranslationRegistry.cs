using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services.Registries;

public interface ITranslationRegistry
{
    IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null);
    IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings);
    ITranslationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null);
    ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null);
    Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings,
                                 IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null);
}

public sealed class TranslationRegistry : ITranslationRegistry
{
    private readonly AppLog _log;
    private readonly ContainerizedServiceProbe? _containerizedProbe;

    public TranslationRegistry(AppLog log, ContainerizedServiceProbe? containerizedProbe = null)
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
                    ProviderNames.Nllb200,
                    "NLLB-200",
                    false,
                    null,
                    [.. GetCpuNllbModels(), .. GetGpuNllbModels()],
                    SupportedRuntimes: [InferenceRuntime.Local, InferenceRuntime.Containerized],
                    DefaultRuntime: InferenceRuntime.Local),
                new(
                    ProviderNames.CTranslate2,
                    "CTranslate2 (Local Lightweight, recommended)",
                    false,
                    null,
                    GetCTranslate2Models(),
                    SupportedRuntimes: [InferenceRuntime.Local],
                    DefaultRuntime: InferenceRuntime.Local,
                    Notes: "Recommended lightweight local translation option."),
                .. GetAvailableProviders(ComputeProfile.Cloud),
            ];
        }

        if (profile == ComputeProfile.Cpu)
        {
            return
            [
                new(
                    ProviderNames.Nllb200,
                    "NLLB-200 (Local CPU)",
                    false,
                    null,
                    GetCpuNllbModels(),
                    SupportedRuntimes: [InferenceRuntime.Local],
                    DefaultRuntime: InferenceRuntime.Local),
                new(
                    ProviderNames.CTranslate2,
                    "CTranslate2 (Local Lightweight, recommended)",
                    false,
                    null,
                    GetCTranslate2Models(),
                    SupportedRuntimes: [InferenceRuntime.Local],
                    DefaultRuntime: InferenceRuntime.Local,
                    Notes: "Recommended lightweight local translation option."),
            ];
        }

        if (profile == ComputeProfile.Gpu)
        {
            return
            [
                new(
                    ProviderNames.Nllb200,
                    "NLLB-200 (Local GPU)",
                    false,
                    null,
                    GetGpuNllbModels(),
                    SupportedRuntimes: [InferenceRuntime.Containerized],
                    DefaultRuntime: InferenceRuntime.Containerized),
            ];
        }

        var providers = new List<ProviderDescriptor>
        {
            new(
                ProviderNames.GoogleTranslateFree,
                "Google Translate (Free — unreliable, web scraper)",
                false,
                null,
                ["default"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud,
                Notes: "Uses googletrans==4.0.0rc1 which scrapes Google's private endpoints. May break without warning when Google changes its API."),
            new(
                ProviderNames.Deepl,
                "DeepL API",
                true,
                CredentialKeys.Deepl,
                ["default"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud,
                IsImplemented: true),
            new(
                ProviderNames.OpenAi,
                "OpenAI API",
                true,
                CredentialKeys.OpenAi,
                ["gpt-4o", "gpt-4o-mini"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud,
                IsImplemented: true),
            new(
                ProviderNames.GeminiTranslation,
                "Google Gemini",
                true,
                CredentialKeys.GoogleGemini,
                ["gemini-2.0-flash", "gemini-2.5-flash-preview-04-17"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud,
                IsImplemented: true),
        };

        return providers;
    }

    public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings)
    {
        var normalizedProviderId = ResolveProviderId(providerId, settings, profile);
        return normalizedProviderId switch
        {
            ProviderNames.Nllb200 when profile == ComputeProfile.Cpu => GetCpuNllbModels(),
            ProviderNames.Nllb200 when profile == ComputeProfile.Gpu => GetGpuNllbModels(),
            ProviderNames.CTranslate2 when profile == ComputeProfile.Cpu => GetCTranslate2Models(),
            _ => GetAvailableProviders(profile)
                .FirstOrDefault(p => p.Id == normalizedProviderId)?.SupportedModels
                ?? ["default"],
        };
    }

    public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null)
    {
        var resolvedProfile = ResolveProfile(providerId, settings, profile);
        var resolvedRuntime = InferenceRuntimeCatalog.ResolveRuntime(resolvedProfile);
        var normalizedProviderId = ResolveProviderId(providerId, settings, resolvedProfile);
        var desc = GetAvailableProviders(resolvedProfile).FirstOrDefault(p => p.Id == normalizedProviderId);
        if (desc == null)
            return new ProviderReadiness(false, $"Unknown translation provider '{normalizedProviderId}'.");
        if (!desc.IsImplemented)
            return new ProviderReadiness(false, $"Provider '{desc.DisplayName}' is not implemented yet.");
        if (desc.RequiresApiKey && string.IsNullOrEmpty(keyStore?.GetKey(desc.CredentialKey!)))
            return new ProviderReadiness(false, $"API key missing for provider '{desc.DisplayName}'.");

        if (resolvedRuntime == InferenceRuntime.Containerized)
            return ContainerizedProviderReadiness.CheckTranslation(settings, _containerizedProbe);

        var provider = CreateProvider(normalizedProviderId, settings, keyStore, resolvedProfile);
        return provider.CheckReadiness(settings, keyStore);
    }

    public async Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings,
                                              IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null)
    {
        var resolvedProfile = ResolveProfile(providerId, settings, profile);
        var resolvedRuntime = InferenceRuntimeCatalog.ResolveRuntime(resolvedProfile);
        var normalizedProviderId = ResolveProviderId(providerId, settings, resolvedProfile);
        var desc = GetAvailableProviders(resolvedProfile).FirstOrDefault(p => p.Id == normalizedProviderId);
        if (desc == null || !desc.IsImplemented) return false;
        var provider = CreateProvider(normalizedProviderId, settings, keyStore, resolvedProfile);
        return await provider.EnsureReadyAsync(settings, progress, ct);
    }

    public ITranslationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null)
    {
        var resolvedProfile = ResolveProfile(providerId, settings, profile);
        var resolvedRuntime = InferenceRuntimeCatalog.ResolveRuntime(resolvedProfile);
        var normalizedProviderId = ResolveProviderId(providerId, settings, resolvedProfile);

        if (resolvedRuntime == InferenceRuntime.Containerized)
        {
            return new ContainerizedTranslationProvider(
                new ContainerizedInferenceClient(settings.EffectiveContainerizedServiceUrl, _log),
                _log,
                settings.TranslationModel);
        }

        return normalizedProviderId switch
        {
            ProviderNames.Nllb200 => new NllbTranslationProvider(_log, settings.TranslationModel),
            ProviderNames.CTranslate2 => new CTranslate2TranslationProvider(_log, settings.TranslationModel),
            ProviderNames.GoogleTranslateFree => new GoogleTranslationProvider(_log),
            ProviderNames.Deepl => new DeepLTranslationProvider(
                _log,
                keyStore?.GetKey(CredentialKeys.Deepl) ?? string.Empty),
            ProviderNames.OpenAi => new OpenAiTranslationProvider(
                _log,
                keyStore?.GetKey(CredentialKeys.OpenAi) ?? string.Empty,
                settings.TranslationModel),
            ProviderNames.GeminiTranslation => new GeminiTranslationProvider(
                _log,
                keyStore?.GetKey(CredentialKeys.GoogleGemini) ?? string.Empty,
                settings.TranslationModel),
            _ => throw new PipelineProviderException(
                $"Translation provider '{providerId}' is not implemented. " +
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

        if (string.Equals(settings.TranslationProvider, providerId, StringComparison.Ordinal))
            return settings.TranslationProfile;

        return InferenceRuntimeCatalog.InferTranslationProfile(providerId);
    }

    private static string ResolveProviderId(string providerId, AppSettings settings, ComputeProfile profile)
    {
        if (!InferenceRuntimeCatalog.IsKnownTranslationProvider(providerId)
            && !string.Equals(providerId, ProviderNames.ContainerizedService, StringComparison.Ordinal))
        {
            return providerId;
        }

        if (string.Equals(settings.TranslationProvider, providerId, StringComparison.Ordinal))
            return InferenceRuntimeCatalog.NormalizeTranslationProvider(profile, settings.TranslationProvider);

        return InferenceRuntimeCatalog.NormalizeTranslationProvider(profile, providerId);
    }

    private static IReadOnlyList<string> GetCpuNllbModels() =>
        ["nllb-200-distilled-600M"];

    private static IReadOnlyList<string> GetGpuNllbModels() =>
        ["nllb-200-distilled-1.3B", "nllb-200-1.3B"];

    private static IReadOnlyList<string> GetCTranslate2Models() =>
        ["nllb-200-distilled-600M"];
}
