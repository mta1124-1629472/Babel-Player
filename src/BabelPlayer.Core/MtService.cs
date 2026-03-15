using System.Collections.Concurrent;
using BabelPlayer.Core.Translation;

namespace BabelPlayer.Core;

public enum CloudTranslationProvider
{
    None,
    OpenAi,
    Google,
    DeepL,
    MicrosoftTranslator
}

public enum OfflineTranslationModel
{
    None,
    HyMt15_1_8B,
    HyMt15_7B
}

public sealed class LocalTranslationRuntimeStatus
{
    public string Stage   { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed record CloudTranslationOptions(
    CloudTranslationProvider Provider,
    string ApiKey,
    string? Model  = null,
    string? Region = null);

public sealed record LocalTranslationOptions(
    OfflineTranslationModel Model,
    string? LlamaServerPath = null);

/// <summary>
/// Thin dispatcher that routes translate calls to the active
/// <see cref="ITranslationProvider"/> (cloud or local) and deduplicates
/// in-flight requests for the same text.
/// </summary>
public class MtService
{
    private readonly ConcurrentDictionary<string, Task<string>> _pending =
        new(StringComparer.Ordinal);

    private readonly IBabelLogger _logger;

    private ITranslationProvider?      _cloudProvider;
    private LlamaCppTranslationProvider? _localProvider;
    private CloudTranslationOptions?   _cloudOptions;
    private LocalTranslationOptions    _localOptions = new(OfflineTranslationModel.None);

    public MtService(IBabelLogFactory? logFactory = null)
    {
        _logger = (logFactory ?? NullBabelLogFactory.Instance).CreateLogger("translation.mt");
    }

    public string LoadedModelPath   { get; private set; } = string.Empty;
    public bool   UseCloudTranslation => _cloudProvider is not null;
    public CloudTranslationProvider CloudProvider => _cloudOptions?.Provider ?? CloudTranslationProvider.None;

    public event Action<LocalTranslationRuntimeStatus>? OnLocalRuntimeStatus;

    // ── Configuration ─────────────────────────────────────────────────────────

    public void ConfigureCloud(CloudTranslationOptions? options)
    {
        _cloudOptions  = null;
        _cloudProvider = null;

        if (options is null || string.IsNullOrWhiteSpace(options.ApiKey)) return;

        var opt = options with { ApiKey = options.ApiKey.Trim() };
        _cloudOptions = opt;
        _cloudProvider = opt.Provider switch
        {
            CloudTranslationProvider.OpenAi             => new OpenAiTranslationProvider(opt.ApiKey, opt.Model),
            CloudTranslationProvider.Google             => new GoogleTranslationProvider(opt.ApiKey),
            CloudTranslationProvider.DeepL              => new DeepLTranslationProvider(opt.ApiKey),
            CloudTranslationProvider.MicrosoftTranslator => opt.Region is not null
                ? new MicrosoftTranslationProvider(opt.ApiKey, opt.Region)
                : null,
            _ => null
        };

        _logger.LogInfo("Cloud translation configured.",
            BabelLogContext.Create(
                ("provider", opt.Provider),
                ("model",    opt.Model),
                ("region",   opt.Region)));
    }

    public void ConfigureLocal(LocalTranslationOptions? options)
    {
        _localProvider?.Dispose();
        _localProvider = null;
        _localOptions  = options ?? new LocalTranslationOptions(OfflineTranslationModel.None);

        LoadedModelPath = _localOptions.Model switch
        {
            OfflineTranslationModel.HyMt15_1_8B => "HY-MT1.5-1.8B (llama.cpp)",
            OfflineTranslationModel.HyMt15_7B   => "HY-MT1.5-7B (llama.cpp)",
            _                                   => string.Empty
        };

        if (_localOptions.Model != OfflineTranslationModel.None
            && !string.IsNullOrWhiteSpace(_localOptions.LlamaServerPath))
        {
            _localProvider = new LlamaCppTranslationProvider(_localOptions.Model, _localOptions.LlamaServerPath);
            _localProvider.OnRuntimeStatus += status => OnLocalRuntimeStatus?.Invoke(status);
        }

        _logger.LogInfo("Local translation configured.",
            BabelLogContext.Create(("model", _localOptions.Model), ("serverPath", _localOptions.LlamaServerPath)));
    }

    // ── Validation (static helpers kept for backward compat) ──────────────────────

    public static async Task ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        var provider = new OpenAiTranslationProvider(apiKey);
        await provider.ValidateAsync(cancellationToken);
    }

    public static async Task ValidateTranslationProviderAsync(
        CloudTranslationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var svc = new MtService();
        svc.ConfigureCloud(options);
        if (svc._cloudProvider is null)
            throw new InvalidOperationException("No valid translation provider could be configured with the supplied options.");
        await svc._cloudProvider.ValidateAsync(cancellationToken);
    }

    // ── Translation ────────────────────────────────────────────────────────────

    public Task<string> TranslateAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(string.Empty);

        var normalized = text.Trim();
        if (string.Equals(LanguageDetector.Detect(normalized), "en", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(normalized);

        var provider = ActiveProvider();
        if (provider is null) return Task.FromResult(normalized);

        var cacheKey = $"{provider.Name}:{normalized}";
        return _pending.GetOrAdd(cacheKey, _ => TranslateOneCoreAsync(cacheKey, normalized, provider, cancellationToken));
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0) return Array.Empty<string>();

        var normalized = texts.Select(t => t?.Trim() ?? string.Empty).ToList();
        if (normalized.All(t => string.Equals(LanguageDetector.Detect(t), "en", StringComparison.OrdinalIgnoreCase)))
            return normalized;

        var provider = ActiveProvider();
        if (provider is null) return normalized;

        _logger.LogInfo("Translation batch starting.",
            BabelLogContext.Create(("provider", provider.Name), ("count", normalized.Count)));

        return await provider.TranslateBatchAsync(normalized, cancellationToken);
    }

    public async Task WarmupLocalRuntimeAsync(CancellationToken cancellationToken)
    {
        if (_localProvider is null) return;
        _logger.LogInfo("Local translation runtime warmup starting.",
            BabelLogContext.Create(("model", _localOptions.Model)));
        await _localProvider.ValidateAsync(cancellationToken);
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    private ITranslationProvider? ActiveProvider() =>
        _cloudProvider ?? (ITranslationProvider?)_localProvider;

    private async Task<string> TranslateOneCoreAsync(
        string cacheKey,
        string text,
        ITranslationProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await provider.TranslateBatchAsync([text], cancellationToken);
            return results[0];
        }
        finally
        {
            _pending.TryRemove(cacheKey, out _);
        }
    }
}
