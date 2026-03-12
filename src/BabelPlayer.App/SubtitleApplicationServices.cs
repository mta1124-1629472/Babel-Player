using System.Diagnostics;
using BabelPlayer.Core;

namespace BabelPlayer.App;

public interface ISubtitleSourceResolver
{
    Task<IReadOnlyList<SubtitleCue>> LoadExternalSubtitleCuesAsync(
        string path,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SubtitleCue>> ExtractEmbeddedSubtitleCuesAsync(
        string videoPath,
        MediaTrackInfo track,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken);
}

public interface ICaptionGenerator
{
    Task<IReadOnlyList<SubtitleCue>> GenerateCaptionsAsync(
        string videoPath,
        TranscriptionModelSelection selection,
        string? languageHint,
        Action<TranscriptChunk>? onFinal,
        Action<ModelTransferProgress>? onProgress,
        CancellationToken cancellationToken);
}

public interface ISubtitleTranslator
{
    event Action<LocalTranslationRuntimeStatus>? RuntimeStatusChanged;

    Task WarmupAsync(TranslationModelSelection selection, CancellationToken cancellationToken);

    Task<string> TranslateAsync(
        TranslationModelSelection selection,
        string text,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationModelSelection selection,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken);
}

public interface IAiCredentialCoordinator
{
    Task<bool> EnsureOpenAiApiKeyAsync(CancellationToken cancellationToken);

    Task<bool> EnsureTranslationProviderCredentialsAsync(TranslationProvider provider, CancellationToken cancellationToken);
}

public interface IRuntimeProvisioner
{
    Task<string> EnsureLlamaCppAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken);

    Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken);
    
    Task<bool> EnsureLlamaCppRuntimeReadyAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken);
}

public sealed class DefaultSubtitleSourceResolver : ISubtitleSourceResolver
{
    private readonly IBabelLogger _logger;

    public DefaultSubtitleSourceResolver(IBabelLogFactory? logFactory = null)
    {
        _logger = (logFactory ?? NullBabelLogFactory.Instance).CreateLogger("subtitles.source");
    }

    public Task<IReadOnlyList<SubtitleCue>> LoadExternalSubtitleCuesAsync(
        string path,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        _logger.LogInfo("Loading external subtitles.", BabelLogContext.Create(("path", path)));
        return SubtitleImportService.LoadExternalSubtitleCuesAsync(path, onRuntimeProgress, onStatus, cancellationToken);
    }

    public Task<IReadOnlyList<SubtitleCue>> ExtractEmbeddedSubtitleCuesAsync(
        string videoPath,
        MediaTrackInfo track,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        _logger.LogInfo("Extracting embedded subtitle track.", BabelLogContext.Create(("videoPath", videoPath), ("trackId", track.Id), ("codec", track.Codec)));
        return SubtitleImportService.ExtractEmbeddedSubtitleCuesAsync(videoPath, track, onRuntimeProgress, onStatus, cancellationToken);
    }
}

public sealed class DefaultCaptionGenerator : ICaptionGenerator
{
    private readonly ProviderAvailabilityContext _context;
    private readonly TranscriptionProviderRegistry _registry;
    private readonly IBabelLogger _logger;

    public DefaultCaptionGenerator(ProviderAvailabilityContext context, TranscriptionProviderRegistry registry, IBabelLogFactory? logFactory = null)
    {
        _context = context;
        _registry = registry;
        _logger = (logFactory ?? context.LogFactory ?? NullBabelLogFactory.Instance).CreateLogger("subtitles.captions");
    }

    public async Task<IReadOnlyList<SubtitleCue>> GenerateCaptionsAsync(
        string videoPath,
        TranscriptionModelSelection selection,
        string? languageHint,
        Action<TranscriptChunk>? onFinal,
        Action<ModelTransferProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        List<Exception>? failures = null;
        var providers = _registry.ResolveProviders(selection, _context);
        var options = new CaptionGenerationOptions
        {
            Mode = selection.Provider == TranscriptionProvider.Cloud ? CaptionTranscriptionMode.Cloud : CaptionTranscriptionMode.Local,
            LanguageHint = languageHint,
            OpenAiApiKey = _context.EnvironmentVariableReader("OPENAI_API_KEY") ?? _context.CredentialFacade.GetOpenAiApiKey(),
            LocalModelType = selection.LocalModelType,
            CloudModel = selection.CloudModel
        };
        _logger.LogInfo("Caption generation starting.", BabelLogContext.Create(("videoPath", videoPath), ("modelKey", selection.Key), ("provider", selection.Provider), ("languageHint", languageHint)));

        foreach (var provider in providers)
        {
            try
            {
                var result = await provider.TranscribeAsync(
                    new TranscriptionRequest(videoPath, options, onFinal, onProgress),
                    _context,
                    cancellationToken);
                _logger.LogInfo("Caption generation completed.", BabelLogContext.Create(("videoPath", videoPath), ("modelKey", selection.Key), ("cueCount", result.Count), ("providerAdapter", provider.Id)));
                return result;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failures ??= [];
                failures.Add(ex);
                _logger.LogWarning("Caption generation provider failed.", ex, BabelLogContext.Create(("videoPath", videoPath), ("modelKey", selection.Key), ("providerAdapter", provider.Id)));
            }
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException($"No transcription provider completed for {selection.DisplayName}.", failures);
        }

        throw new InvalidOperationException($"No transcription provider is available for {selection.DisplayName}.");
    }
}

public sealed class ProviderBackedSubtitleTranslator : ISubtitleTranslator
{
    private readonly ProviderAvailabilityContext _context;
    private readonly TranslationProviderRegistry _registry;
    private readonly IBabelLogger _logger;

    public ProviderBackedSubtitleTranslator(ProviderAvailabilityContext context, TranslationProviderRegistry registry, IBabelLogFactory? logFactory = null)
    {
        _context = context;
        _registry = registry;
        _logger = (logFactory ?? context.LogFactory ?? NullBabelLogFactory.Instance).CreateLogger("subtitles.translation");
    }

    public event Action<LocalTranslationRuntimeStatus>? RuntimeStatusChanged;

    public async Task WarmupAsync(TranslationModelSelection selection, CancellationToken cancellationToken)
    {
        if (selection.Provider is not (TranslationProvider.LocalHyMt15_1_8B or TranslationProvider.LocalHyMt15_7B))
        {
            return;
        }

        _logger.LogInfo("Translation runtime warmup starting.", BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider)));
        var service = new MtService(_context.LogFactory);
        service.OnLocalRuntimeStatus += HandleRuntimeStatus;
        service.ConfigureLocal(selection.Provider switch
        {
            TranslationProvider.LocalHyMt15_1_8B => new LocalTranslationOptions(
                OfflineTranslationModel.HyMt15_1_8B,
                ProviderAvailabilityUtilities.ResolveLlamaCppServerPath(_context)),
            TranslationProvider.LocalHyMt15_7B => new LocalTranslationOptions(
                OfflineTranslationModel.HyMt15_7B,
                ProviderAvailabilityUtilities.ResolveLlamaCppServerPath(_context)),
            _ => new LocalTranslationOptions(OfflineTranslationModel.None)
        });

        try
        {
            await service.WarmupLocalRuntimeAsync(cancellationToken);
            _logger.LogInfo("Translation runtime warmup completed.", BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider)));
        }
        finally
        {
            service.OnLocalRuntimeStatus -= HandleRuntimeStatus;
        }
    }

    public async Task<string> TranslateAsync(
        TranslationModelSelection selection,
        string text,
        CancellationToken cancellationToken)
    {
        var translated = await TranslateBatchAsync(selection, [text], cancellationToken);
        return translated[0];
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationModelSelection selection,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        if (selection.Provider == TranslationProvider.None)
        {
            return texts.ToArray();
        }

        if (!_registry.TryGetProvider(selection.Provider, out var provider) || provider is null)
        {
            throw new InvalidOperationException($"No translation provider adapter is registered for {selection.Provider}.");
        }

        _logger.LogInfo("Translation batch starting.", BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider), ("textCount", texts.Count)));
        try
        {
            var result = await provider.TranslateBatchAsync(
                new TranslationRequest(selection, texts, "en"),
                _context,
                cancellationToken);
            _logger.LogInfo("Translation batch completed.", BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider), ("textCount", texts.Count)));
            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Translation batch failed.", ex, BabelLogContext.Create(("modelKey", selection.Key), ("provider", selection.Provider), ("textCount", texts.Count)));
            throw;
        }
    }

    private void HandleRuntimeStatus(LocalTranslationRuntimeStatus status)
    {
        RuntimeStatusChanged?.Invoke(status);
    }
}

public sealed class DefaultAiCredentialCoordinator : IAiCredentialCoordinator
{
    private readonly CredentialFacade _credentialFacade;
    private readonly ICredentialDialogService? _credentialDialogService;
    private readonly Func<string, string?> _environmentVariableReader;
    private readonly Func<string, CancellationToken, Task> _validateOpenAiApiKeyAsync;
    private readonly Func<CloudTranslationOptions, CancellationToken, Task> _validateTranslationProviderAsync;

    public DefaultAiCredentialCoordinator(
        CredentialFacade credentialFacade,
        ICredentialDialogService? credentialDialogService,
        Func<string, string?> environmentVariableReader,
        Func<string, CancellationToken, Task> validateOpenAiApiKeyAsync,
        Func<CloudTranslationOptions, CancellationToken, Task> validateTranslationProviderAsync)
    {
        _credentialFacade = credentialFacade;
        _credentialDialogService = credentialDialogService;
        _environmentVariableReader = environmentVariableReader;
        _validateOpenAiApiKeyAsync = validateOpenAiApiKeyAsync;
        _validateTranslationProviderAsync = validateTranslationProviderAsync;
    }

    public async Task<bool> EnsureOpenAiApiKeyAsync(CancellationToken cancellationToken)
    {
        var apiKey = _environmentVariableReader("OPENAI_API_KEY") ?? _credentialFacade.GetOpenAiApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await _validateOpenAiApiKeyAsync(apiKey, cancellationToken);
            return true;
        }

        if (_credentialDialogService is null)
        {
            return false;
        }

        var submitted = await _credentialDialogService.PromptForApiKeyAsync(
            "OpenAI API Key",
            "Enter the OpenAI API key for cloud transcription and translation.",
            "Save",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(submitted))
        {
            return false;
        }

        await _validateOpenAiApiKeyAsync(submitted, cancellationToken);
        _credentialFacade.SaveOpenAiApiKey(submitted.Trim());
        return true;
    }

    public async Task<bool> EnsureTranslationProviderCredentialsAsync(TranslationProvider provider, CancellationToken cancellationToken)
    {
        return provider switch
        {
            TranslationProvider.None => false,
            TranslationProvider.OpenAi => await EnsureOpenAiApiKeyAsync(cancellationToken),
            TranslationProvider.Google => await EnsureSingleApiKeyProviderAsync(
                "Google Translate API Key",
                "Enter the Google Translate API key.",
                apiKey => _credentialFacade.SaveGoogleTranslateApiKey(apiKey),
                apiKey => new CloudTranslationOptions(CloudTranslationProvider.Google, apiKey),
                _environmentVariableReader("GOOGLE_TRANSLATE_API_KEY")
                    ?? _environmentVariableReader("GOOGLE_CLOUD_TRANSLATE_API_KEY")
                    ?? _credentialFacade.GetGoogleTranslateApiKey(),
                cancellationToken),
            TranslationProvider.DeepL => await EnsureSingleApiKeyProviderAsync(
                "DeepL API Key",
                "Enter the DeepL API key.",
                apiKey => _credentialFacade.SaveDeepLApiKey(apiKey),
                apiKey => new CloudTranslationOptions(CloudTranslationProvider.DeepL, apiKey),
                _environmentVariableReader("DEEPL_API_KEY") ?? _credentialFacade.GetDeepLApiKey(),
                cancellationToken),
            TranslationProvider.MicrosoftTranslator => await EnsureMicrosoftCredentialsAsync(cancellationToken),
            TranslationProvider.LocalHyMt15_1_8B or TranslationProvider.LocalHyMt15_7B => true,
            _ => false
        };
    }

    private async Task<bool> EnsureSingleApiKeyProviderAsync(
        string title,
        string message,
        Action<string> persist,
        Func<string, CloudTranslationOptions> buildOptions,
        string? existingApiKey,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(existingApiKey))
        {
            await _validateTranslationProviderAsync(buildOptions(existingApiKey.Trim()), cancellationToken);
            return true;
        }

        if (_credentialDialogService is null)
        {
            return false;
        }

        var submitted = await _credentialDialogService.PromptForApiKeyAsync(title, message, "Save", cancellationToken);
        if (string.IsNullOrWhiteSpace(submitted))
        {
            return false;
        }

        await _validateTranslationProviderAsync(buildOptions(submitted.Trim()), cancellationToken);
        persist(submitted.Trim());
        return true;
    }

    private async Task<bool> EnsureMicrosoftCredentialsAsync(CancellationToken cancellationToken)
    {
        var apiKey = _environmentVariableReader("MICROSOFT_TRANSLATOR_API_KEY")
                     ?? _environmentVariableReader("AZURE_TRANSLATOR_KEY")
                     ?? _credentialFacade.GetMicrosoftTranslatorApiKey();
        var region = _environmentVariableReader("MICROSOFT_TRANSLATOR_REGION")
                     ?? _environmentVariableReader("AZURE_TRANSLATOR_REGION")
                     ?? _credentialFacade.GetMicrosoftTranslatorRegion();

        if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(region))
        {
            await _validateTranslationProviderAsync(
                new CloudTranslationOptions(CloudTranslationProvider.MicrosoftTranslator, apiKey.Trim(), null, region.Trim()),
                cancellationToken);
            return true;
        }

        if (_credentialDialogService is null)
        {
            return false;
        }

        var submitted = await _credentialDialogService.PromptForApiKeyWithRegionAsync(
            "Microsoft Translator",
            "Enter the Microsoft Translator API key and region.",
            "Save",
            cancellationToken);
        if (submitted is null || string.IsNullOrWhiteSpace(submitted.Value.ApiKey) || string.IsNullOrWhiteSpace(submitted.Value.Region))
        {
            return false;
        }

        await _validateTranslationProviderAsync(
            new CloudTranslationOptions(CloudTranslationProvider.MicrosoftTranslator, submitted.Value.ApiKey.Trim(), null, submitted.Value.Region.Trim()),
            cancellationToken);
        _credentialFacade.SaveMicrosoftTranslatorApiKey(submitted.Value.ApiKey.Trim());
        _credentialFacade.SaveMicrosoftTranslatorRegion(submitted.Value.Region.Trim());
        return true;
    }
}

public sealed class DefaultRuntimeProvisioner : IRuntimeProvisioner
{
    private readonly IRuntimeBootstrapService _runtimeBootstrapService;
    private readonly CredentialFacade? _credentialFacade;
    private readonly ICredentialDialogService? _credentialDialogService;
    private readonly IFilePickerService? _filePickerService;
    private readonly Func<string, string?> _environmentVariableReader;
    private readonly IBabelLogger _logger;

    public DefaultRuntimeProvisioner(
        IRuntimeBootstrapService runtimeBootstrapService,
        CredentialFacade? credentialFacade = null,
        ICredentialDialogService? credentialDialogService = null,
        IFilePickerService? filePickerService = null,
        Func<string, string?>? environmentVariableReader = null,
        IBabelLogFactory? logFactory = null)
    {
        _runtimeBootstrapService = runtimeBootstrapService;
        _credentialFacade = credentialFacade;
        _credentialDialogService = credentialDialogService;
        _filePickerService = filePickerService;
        _environmentVariableReader = environmentVariableReader ?? Environment.GetEnvironmentVariable;
        _logger = (logFactory ?? NullBabelLogFactory.Instance).CreateLogger("runtime.provisioner");
    }

    public Task<string> EnsureLlamaCppAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => _runtimeBootstrapService.EnsureLlamaCppAsync(onProgress, cancellationToken);

    public Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
        => _runtimeBootstrapService.EnsureFfmpegAsync(onProgress, cancellationToken);

    public async Task<bool> EnsureLlamaCppRuntimeReadyAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken)
    {
        if (TryResolveLlamaCppServerPath() is not null)
        {
            _logger.LogInfo("llama.cpp runtime already available.");
            return true;
        }

        if (_credentialDialogService is null)
        {
            return false;
        }

        var choice = await _credentialDialogService.PromptForLlamaCppBootstrapChoiceAsync(
            "llama.cpp Setup",
            "Local HY-MT translation needs llama-server. Install it automatically or choose an existing executable.",
            cancellationToken);
        _logger.LogInfo("llama.cpp bootstrap choice received.", BabelLogContext.Create(("choice", choice)));

        switch (choice)
        {
            case LlamaCppBootstrapChoice.InstallAutomatically:
                {
                    var serverPath = await _runtimeBootstrapService.EnsureLlamaCppAsync(onProgress, cancellationToken);
                    if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
                    {
                        return false;
                    }

                    _credentialFacade?.SaveLlamaCppServerPath(serverPath);
                    _credentialFacade?.SaveLlamaCppRuntimeVersion(LlamaCppRuntimeInstaller.RuntimeVersion);
                    _credentialFacade?.SaveLlamaCppRuntimeSource(LlamaCppRuntimeInstaller.RuntimeSource);
                    _logger.LogInfo("llama.cpp runtime installed automatically.", BabelLogContext.Create(("serverPath", serverPath)));
                    return true;
                }
            case LlamaCppBootstrapChoice.ChooseExisting:
                {
                    if (_filePickerService is null)
                    {
                        return false;
                    }

                    var selectedPath = await _filePickerService.PickExecutableAsync(
                        "Choose llama-server",
                        "llama.cpp server",
                        [".exe"],
                        cancellationToken);
                    if (string.IsNullOrWhiteSpace(selectedPath) || !File.Exists(selectedPath))
                    {
                        return false;
                    }

                    _credentialFacade?.SaveLlamaCppServerPath(selectedPath);
                    _credentialFacade?.SaveLlamaCppRuntimeSource("manual");
                    _logger.LogInfo("llama.cpp runtime path selected manually.", BabelLogContext.Create(("serverPath", selectedPath)));
                    return true;
                }
            case LlamaCppBootstrapChoice.OpenOfficialDownloadPage:
                _logger.LogInfo("Opening llama.cpp download page.");
                Process.Start(new ProcessStartInfo
                {
                    FileName = LlamaCppRuntimeInstaller.ReleasePageUrl,
                    UseShellExecute = true
                });
                return false;
            default:
                return false;
        }
    }

    private string? TryResolveLlamaCppServerPath()
    {
        var configuredPath = _environmentVariableReader("LLAMA_SERVER_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        configuredPath = _credentialFacade?.GetLlamaCppServerPath();
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var installedPath = LlamaCppRuntimeInstaller.GetInstalledServerPath();
        return File.Exists(installedPath) ? installedPath : null;
    }
}
