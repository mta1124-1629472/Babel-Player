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
    IReadOnlyList<ProviderDescriptor> GetAvailableProviders();
    ITranslationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null);
    ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore);
    Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings,
                                 IProgress<double>? progress = null, CancellationToken ct = default);
}

public sealed class TranslationRegistry : ITranslationRegistry
{
    private readonly AppLog _log;

    public TranslationRegistry(AppLog log)
    {
        _log = log;
    }

    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders() =>
    [
        new ProviderDescriptor(
            ProviderNames.GoogleTranslateFree,
            "Google Translate (Free — unreliable, web scraper)",
            false,
            null,
            ["default"],
            Notes: "Uses googletrans==4.0.0rc1 which scrapes Google's private endpoints. May break without warning when Google changes its API."),
        new ProviderDescriptor(
            ProviderNames.Nllb200,
            "NLLB-200 (Local)",
            false,
            null,
            ["nllb-200-distilled-600M", "nllb-200-distilled-1.3B", "nllb-200-1.3B"]),
        new ProviderDescriptor(
            ProviderNames.ContainerizedService,
            "Containerized Inference Service",
            false,
            null,
            ["default"]),
        new ProviderDescriptor(
            ProviderNames.Deepl,
            "DeepL API",
            true,
            CredentialKeys.Deepl,
            ["default"],
            IsImplemented: false),
        new ProviderDescriptor(
            ProviderNames.OpenAi,
            "OpenAI API",
            true,
            CredentialKeys.OpenAi,
            ["gpt-4o", "gpt-4o-mini"],
            IsImplemented: false)
    ];

    public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore)
    {
        var desc = GetAvailableProviders().FirstOrDefault(p => p.Id == providerId);
        if (desc == null)
            return new ProviderReadiness(false, $"Unknown translation provider '{providerId}'.");
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

    public ITranslationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null)
    {
        return providerId switch
        {
            ProviderNames.Nllb200 => new NllbTranslationProvider(_log, settings.TranslationModel),
            ProviderNames.GoogleTranslateFree => new GoogleTranslationProvider(_log),
            ProviderNames.ContainerizedService => new ContainerizedTranslationProvider(
                new ContainerizedInferenceClient(settings.ContainerizedServiceUrl, _log), _log),
            _ => throw new PipelineProviderException(
                $"Translation provider '{providerId}' is not implemented. " +
                "Select an implemented provider in Settings.")
        };
    }
}
