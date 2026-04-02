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
    IReadOnlyList<ProviderDescriptor> GetAvailableProviders(InferenceRuntime? runtime = null);
    ITranslationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, InferenceRuntime? runtime = null);
    ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, InferenceRuntime? runtime = null);
    Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings,
                                 IProgress<double>? progress = null, CancellationToken ct = default, InferenceRuntime? runtime = null);
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

    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(InferenceRuntime? runtime = null)
    {
        var providers = new List<ProviderDescriptor>
        {
            new(
                ProviderNames.GoogleTranslateFree,
                "Google Translate (Free — unreliable, web scraper)",
                false,
                null,
                ["default"],
                SupportedRuntimes: [InferenceRuntime.Cloud, InferenceRuntime.Containerized],
                DefaultRuntime: InferenceRuntime.Cloud,
                Notes: "Uses googletrans==4.0.0rc1 which scrapes Google's private endpoints. May break without warning when Google changes its API."),
            new(
                ProviderNames.Nllb200,
                "NLLB-200 (Local)",
                false,
                null,
                ["nllb-200-distilled-600M", "nllb-200-distilled-1.3B", "nllb-200-1.3B"],
                SupportedRuntimes: [InferenceRuntime.Local],
                DefaultRuntime: InferenceRuntime.Local),
            new(
                ProviderNames.Deepl,
                "DeepL API",
                true,
                CredentialKeys.Deepl,
                ["default"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud),
            new(
                ProviderNames.OpenAi,
                "OpenAI API",
                true,
                CredentialKeys.OpenAi,
                ["gpt-4o", "gpt-4o-mini"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud),
        };

        return runtime is null
            ? providers
            : [.. providers.Where(p => p.EffectiveSupportedRuntimes.Contains(runtime.Value))];
    }

    public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, InferenceRuntime? runtime = null)
    {
        var resolvedRuntime = ResolveRuntime(providerId, settings, runtime);
        var normalizedProviderId = InferenceRuntimeCatalog.NormalizeTranslationProvider(resolvedRuntime, providerId);
        var desc = GetAvailableProviders(resolvedRuntime).FirstOrDefault(p => p.Id == normalizedProviderId);
        if (desc == null)
            return new ProviderReadiness(false, $"Unknown translation provider '{normalizedProviderId}'.");
        if (!desc.IsImplemented)
            return new ProviderReadiness(false, $"Provider '{desc.DisplayName}' is not implemented yet.");
        if (desc.RequiresApiKey && string.IsNullOrEmpty(keyStore?.GetKey(desc.CredentialKey!)))
            return new ProviderReadiness(false, $"API key missing for provider '{desc.DisplayName}'.");

        if (resolvedRuntime == InferenceRuntime.Containerized)
            return ContainerizedProviderReadiness.CheckTranslation(settings, _containerizedProbe);

        var provider = CreateProvider(normalizedProviderId, settings, keyStore, resolvedRuntime);
        return provider.CheckReadiness(settings, keyStore);
    }

    public async Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings,
                                              IProgress<double>? progress = null, CancellationToken ct = default, InferenceRuntime? runtime = null)
    {
        var resolvedRuntime = ResolveRuntime(providerId, settings, runtime);
        var normalizedProviderId = InferenceRuntimeCatalog.NormalizeTranslationProvider(resolvedRuntime, providerId);
        var desc = GetAvailableProviders(resolvedRuntime).FirstOrDefault(p => p.Id == normalizedProviderId);
        if (desc == null || !desc.IsImplemented) return false;
        var provider = CreateProvider(normalizedProviderId, settings, null, resolvedRuntime);
        return await provider.EnsureReadyAsync(settings, progress, ct);
    }

    public ITranslationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, InferenceRuntime? runtime = null)
    {
        var resolvedRuntime = ResolveRuntime(providerId, settings, runtime);
        var normalizedProviderId = InferenceRuntimeCatalog.NormalizeTranslationProvider(resolvedRuntime, providerId);

        if (resolvedRuntime == InferenceRuntime.Containerized)
        {
            return new ContainerizedTranslationProvider(
                new ContainerizedInferenceClient(settings.EffectiveContainerizedServiceUrl, _log),
                _log);
        }

        return normalizedProviderId switch
        {
            ProviderNames.Nllb200 => new NllbTranslationProvider(_log, settings.TranslationModel),
            ProviderNames.GoogleTranslateFree => new GoogleTranslationProvider(_log),
            ProviderNames.Deepl => new DeepLTranslationProvider(
                _log,
                keyStore?.GetKey(CredentialKeys.Deepl) ?? string.Empty),
            ProviderNames.OpenAi => new OpenAiTranslationProvider(
                _log,
                keyStore?.GetKey(CredentialKeys.OpenAi) ?? string.Empty,
                settings.TranslationModel),
            _ => throw new PipelineProviderException(
                $"Translation provider '{providerId}' is not implemented. " +
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

        if (string.Equals(settings.TranslationProvider, providerId, StringComparison.Ordinal))
            return settings.TranslationRuntime;

        return InferenceRuntimeCatalog.InferTranslationRuntime(providerId);
    }
}
