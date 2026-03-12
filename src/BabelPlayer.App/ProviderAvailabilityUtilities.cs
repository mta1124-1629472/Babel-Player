using BabelPlayer.Core;

namespace BabelPlayer.App;

internal static class ProviderAvailabilityUtilities
{
    public static bool HasOpenAiApiKey(ProviderAvailabilityContext context)
    {
        return !string.IsNullOrWhiteSpace(context.EnvironmentVariableReader("OPENAI_API_KEY"))
               || !string.IsNullOrWhiteSpace(context.CredentialFacade.GetOpenAiApiKey());
    }

    public static string? ResolveLlamaCppServerPath(ProviderAvailabilityContext context)
    {
        var configuredPath = context.EnvironmentVariableReader("LLAMA_SERVER_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        configuredPath = context.CredentialFacade.GetLlamaCppServerPath();
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var installedPath = LlamaCppRuntimeInstaller.GetInstalledServerPath();
        if (File.Exists(installedPath))
        {
            return installedPath;
        }

        var pathValue = context.EnvironmentVariableReader("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var exePath = Path.Combine(segment, "llama-server.exe");
            if (File.Exists(exePath))
            {
                return exePath;
            }

            var noExtensionPath = Path.Combine(segment, "llama-server");
            if (File.Exists(noExtensionPath))
            {
                return noExtensionPath;
            }
        }

        return null;
    }

    public static AsrService BuildAsrService(TranscriptionRequest request, ProviderAvailabilityContext context)
    {
        var service = new AsrService(
            request.Options.Mode == CaptionTranscriptionMode.Cloud ? "transcription.cloud" : "transcription.local",
            context.LogFactory);
        if (request.OnFinal is not null)
        {
            service.OnFinal += request.OnFinal;
        }

        if (request.OnProgress is not null)
        {
            service.OnModelTransferProgress += request.OnProgress;
        }

        return service;
    }

    public static Task<IReadOnlyList<string>> TranslateWithMtServiceAsync(
        IReadOnlyList<string> texts,
        CloudTranslationOptions? cloud,
        LocalTranslationOptions? local,
        CancellationToken cancellationToken,
        IBabelLogFactory? logFactory = null)
    {
        var service = new MtService(logFactory);
        service.ConfigureCloud(cloud);
        service.ConfigureLocal(local);
        return service.TranslateBatchAsync(texts, cancellationToken);
    }

    public static CloudTranslationOptions? TryGetGoogleOptions(ProviderAvailabilityContext context)
    {
        var apiKey = context.EnvironmentVariableReader("GOOGLE_TRANSLATE_API_KEY")
                     ?? context.EnvironmentVariableReader("GOOGLE_CLOUD_TRANSLATE_API_KEY")
                     ?? context.CredentialFacade.GetGoogleTranslateApiKey();
        return string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new CloudTranslationOptions(CloudTranslationProvider.Google, apiKey.Trim());
    }

    public static CloudTranslationOptions? TryGetDeepLOptions(ProviderAvailabilityContext context)
    {
        var apiKey = context.EnvironmentVariableReader("DEEPL_API_KEY")
                     ?? context.CredentialFacade.GetDeepLApiKey();
        return string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new CloudTranslationOptions(CloudTranslationProvider.DeepL, apiKey.Trim());
    }

    public static CloudTranslationOptions? TryGetMicrosoftOptions(ProviderAvailabilityContext context)
    {
        var apiKey = context.EnvironmentVariableReader("MICROSOFT_TRANSLATOR_API_KEY")
                     ?? context.EnvironmentVariableReader("AZURE_TRANSLATOR_KEY")
                     ?? context.CredentialFacade.GetMicrosoftTranslatorApiKey();
        var region = context.EnvironmentVariableReader("MICROSOFT_TRANSLATOR_REGION")
                     ?? context.EnvironmentVariableReader("AZURE_TRANSLATOR_REGION")
                     ?? context.CredentialFacade.GetMicrosoftTranslatorRegion();

        return string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(region)
            ? null
            : new CloudTranslationOptions(CloudTranslationProvider.MicrosoftTranslator, apiKey.Trim(), null, region.Trim());
    }
}
