using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
    public string Stage { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed record CloudTranslationOptions(
    CloudTranslationProvider Provider,
    string ApiKey,
    string? Model = null,
    string? Region = null);

public sealed record LocalTranslationOptions(
    OfflineTranslationModel Model,
    string? LlamaServerPath = null);

public class MtService
{
    private const int LlamaServerPort = 8097;
    private const string LlamaServerBaseUrl = "http://127.0.0.1:8097";

    private static readonly HttpClient HttpClient = new();
    private static readonly SemaphoreSlim LlamaServerLock = new(1, 1);

    private static Process? _llamaServerProcess;
    private static OfflineTranslationModel _activeLlamaModel = OfflineTranslationModel.None;
    private static string? _activeLlamaServerPath;

    private readonly ConcurrentDictionary<string, Task<string>> _pendingTranslations = new(StringComparer.Ordinal);
    private CloudTranslationOptions? _cloudOptions;
    private LocalTranslationOptions _localOptions = new(OfflineTranslationModel.None);

    public string LoadedModelPath { get; private set; } = string.Empty;
    public bool UseCloudTranslation => _cloudOptions is not null && !string.IsNullOrWhiteSpace(_cloudOptions.ApiKey);
    public CloudTranslationProvider CloudProvider => _cloudOptions?.Provider ?? CloudTranslationProvider.None;
    public event Action<LocalTranslationRuntimeStatus>? OnLocalRuntimeStatus;

    public async Task WarmupLocalRuntimeAsync(CancellationToken cancellationToken)
    {
        if (_localOptions.Model == OfflineTranslationModel.None)
        {
            return;
        }

        await EnsureLlamaServerAsync(_localOptions, cancellationToken);
    }

    public void ConfigureCloud(CloudTranslationOptions? options)
    {
        _cloudOptions = string.IsNullOrWhiteSpace(options?.ApiKey)
            ? null
            : options with { ApiKey = options.ApiKey.Trim() };
    }

    public void ConfigureLocal(LocalTranslationOptions? options)
    {
        _localOptions = options ?? new LocalTranslationOptions(OfflineTranslationModel.None);
        LoadedModelPath = _localOptions.Model switch
        {
            OfflineTranslationModel.HyMt15_1_8B => "HY-MT1.5-1.8B (llama.cpp)",
            OfflineTranslationModel.HyMt15_7B => "HY-MT1.5-7B (llama.cpp)",
            _ => string.Empty
        };
    }

    public static async Task ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI API key validation failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    public static async Task ValidateTranslationProviderAsync(CloudTranslationOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var result = await TranslateBatchWithProviderAsync(options, ["hola"], "en", cancellationToken);
        if (result.Count != 1 || string.IsNullOrWhiteSpace(result[0]))
        {
            throw new InvalidOperationException("Translation provider validation returned no result.");
        }
    }

    public Task<string> TranslateAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(string.Empty);
        }

        var normalized = text.Trim();
        if (string.Equals(LanguageDetector.Detect(normalized), "en", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(normalized);
        }

        if (UseCloudTranslation && _cloudOptions is not null)
        {
            var cacheKey = $"{_cloudOptions.Provider}:{_cloudOptions.Model}:{normalized}";
            return _pendingTranslations.GetOrAdd(cacheKey, _ => TranslateWithCloudCoreAsync(cacheKey, normalized, _cloudOptions, cancellationToken));
        }

        if (_localOptions.Model != OfflineTranslationModel.None)
        {
            var cacheKey = $"{_localOptions.Model}:{normalized}";
            return _pendingTranslations.GetOrAdd(cacheKey, _ => TranslateWithOfflineModelCoreAsync(cacheKey, normalized, _localOptions, cancellationToken));
        }

        return Task.FromResult(normalized);
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return Array.Empty<string>();
        }

        var normalizedTexts = texts.Select(text => text?.Trim() ?? string.Empty).ToList();
        if (normalizedTexts.All(text => string.Equals(LanguageDetector.Detect(text), "en", StringComparison.OrdinalIgnoreCase)))
        {
            return normalizedTexts;
        }

        if (UseCloudTranslation && _cloudOptions is not null)
        {
            return await TranslateBatchWithProviderAsync(_cloudOptions, normalizedTexts, "en", cancellationToken);
        }

        if (_localOptions.Model != OfflineTranslationModel.None)
        {
            return await TranslateBatchWithOfflineModelAsync(_localOptions, normalizedTexts, cancellationToken);
        }

        return normalizedTexts;
    }

    private async Task<string> TranslateWithCloudCoreAsync(string cacheKey, string text, CloudTranslationOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var translated = await TranslateBatchWithProviderAsync(options, [text], "en", cancellationToken);
            return translated[0];
        }
        finally
        {
            _pendingTranslations.TryRemove(cacheKey, out _);
        }
    }

    private async Task<string> TranslateWithOfflineModelCoreAsync(string cacheKey, string text, LocalTranslationOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var translated = await TranslateBatchWithOfflineModelAsync(options, [text], cancellationToken);
            return translated[0];
        }
        finally
        {
            _pendingTranslations.TryRemove(cacheKey, out _);
        }
    }

    private static Task<IReadOnlyList<string>> TranslateBatchWithProviderAsync(
        CloudTranslationOptions options,
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        return options.Provider switch
        {
            CloudTranslationProvider.OpenAi => TranslateWithOpenAiBatchAsync(options, texts, cancellationToken),
            CloudTranslationProvider.Google => TranslateWithGoogleBatchAsync(options, texts, targetLanguage, cancellationToken),
            CloudTranslationProvider.DeepL => TranslateWithDeepLBatchAsync(options, texts, cancellationToken),
            CloudTranslationProvider.MicrosoftTranslator => TranslateWithMicrosoftBatchAsync(options, texts, targetLanguage, cancellationToken),
            _ => Task.FromResult<IReadOnlyList<string>>(texts.ToList())
        };
    }

    private async Task<IReadOnlyList<string>> TranslateBatchWithOfflineModelAsync(
        LocalTranslationOptions options,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        if (options.Model is OfflineTranslationModel.None)
        {
            return texts.ToList();
        }

        await EnsureLlamaServerAsync(options, cancellationToken);

        var results = new List<string>(texts.Count);
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await TranslateWithLlamaServerAsync(text, cancellationToken));
        }

        return results;
    }

    private async Task EnsureLlamaServerAsync(LocalTranslationOptions options, CancellationToken cancellationToken)
    {
        await LlamaServerLock.WaitAsync(cancellationToken);
        try
        {
            var resolvedServerPath = ResolveLlamaServerPath(options.LlamaServerPath);
            if (string.IsNullOrWhiteSpace(resolvedServerPath))
            {
                throw new InvalidOperationException("llama-server.exe was not found. Set it from Translation > Set llama.cpp Server Path.");
            }

            if (_llamaServerProcess is not null
                && !_llamaServerProcess.HasExited
                && _activeLlamaModel == options.Model
                && string.Equals(_activeLlamaServerPath, resolvedServerPath, StringComparison.OrdinalIgnoreCase)
                && await IsLlamaServerReadyAsync(cancellationToken))
            {
                return;
            }

            StopLlamaServer();
            PublishLocalRuntimeStatus("launching", $"Launching {GetOfflineModelLabel(options.Model)} runtime...");

            var startInfo = new ProcessStartInfo
            {
                FileName = resolvedServerPath,
                Arguments = GetLlamaServerArguments(options.Model),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(resolvedServerPath) ?? Environment.CurrentDirectory
            };

            _llamaServerProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start llama-server.");
            _activeLlamaModel = options.Model;
            _activeLlamaServerPath = resolvedServerPath;

            PublishLocalRuntimeStatus("downloading-model", $"Downloading {GetOfflineModelLabel(options.Model)} model on first use...");
            await WaitForLlamaServerReadyAsync(options.Model, cancellationToken);
            PublishLocalRuntimeStatus("ready", $"{GetOfflineModelLabel(options.Model)} is ready.");
        }
        finally
        {
            LlamaServerLock.Release();
        }
    }

    private async Task WaitForLlamaServerReadyAsync(OfflineTranslationModel model, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var loadingPublished = false;
        while (DateTime.UtcNow - startedAt < TimeSpan.FromMinutes(10))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_llamaServerProcess?.HasExited == true)
            {
                var error = await _llamaServerProcess.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException($"llama-server exited before becoming ready. {error}".Trim());
            }

            if (!loadingPublished && DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(12))
            {
                loadingPublished = true;
                PublishLocalRuntimeStatus("loading-model", $"Loading {GetOfflineModelLabel(model)} model...");
            }

            if (await IsLlamaServerReadyAsync(cancellationToken))
            {
                return;
            }

            await Task.Delay(1000, cancellationToken);
        }

        throw new InvalidOperationException("Timed out waiting for llama-server to become ready. The first model download can take several minutes.");
    }

    private static async Task<bool> IsLlamaServerReadyAsync(CancellationToken cancellationToken)
    {
        if (_llamaServerProcess is null || _llamaServerProcess.HasExited)
        {
            return false;
        }

        try
        {
            using var healthResponse = await HttpClient.GetAsync($"{LlamaServerBaseUrl}/health", cancellationToken);
            if (healthResponse.IsSuccessStatusCode)
            {
                return true;
            }
        }
        catch
        {
        }

        try
        {
            using var modelsResponse = await HttpClient.GetAsync($"{LlamaServerBaseUrl}/v1/models", cancellationToken);
            return modelsResponse.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> TranslateWithLlamaServerAsync(string text, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{LlamaServerBaseUrl}/v1/chat/completions");

        var payload = new
        {
            model = "local",
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = BuildHyMtTranslationPrompt(text)
                }
            },
            temperature = 0.2,
            top_p = 0.6,
            max_tokens = Math.Max(96, Math.Min(768, text.Length * 4)),
            stream = false
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HY-MT local translation failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var choice = document.RootElement.GetProperty("choices")[0];
        if (choice.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var contentElement))
        {
            var translated = contentElement.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(translated))
            {
                return translated;
            }
        }

        if (choice.TryGetProperty("text", out var textElement))
        {
            var translated = textElement.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(translated))
            {
                return translated;
            }
        }

        throw new InvalidOperationException("HY-MT local translation returned no text.");
    }

    private static string BuildHyMtTranslationPrompt(string text)
    {
        var language = LanguageDetector.Detect(text);
        if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return $"将以下文本翻译为英语，只输出翻译结果，不要添加解释：\n\n{text}";
        }

        return $"Translate the following text into English. Return only the translation with no commentary:\n\n{text}";
    }

    private static string GetLlamaServerArguments(OfflineTranslationModel model)
    {
        return model switch
        {
            OfflineTranslationModel.HyMt15_1_8B => "--hf-repo tencent/HY-MT1.5-1.8B-GGUF --hf-file HY-MT1.5-1.8B-Q8_0.gguf --host 127.0.0.1 --port 8097 -c 4096",
            OfflineTranslationModel.HyMt15_7B => "--hf-repo tencent/HY-MT1.5-7B-GGUF --hf-file HY-MT1.5-7B-Q4_K_M.gguf --host 127.0.0.1 --port 8097 -c 4096",
            _ => throw new InvalidOperationException("No llama.cpp server arguments are defined for the selected model.")
        };
    }

    private static string? ResolveLlamaServerPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath.Trim();
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidateExe = Path.Combine(segment, "llama-server.exe");
            if (File.Exists(candidateExe))
            {
                return candidateExe;
            }

            var candidateNoExt = Path.Combine(segment, "llama-server");
            if (File.Exists(candidateNoExt))
            {
                return candidateNoExt;
            }
        }

        return null;
    }

    private static void StopLlamaServer()
    {
        if (_llamaServerProcess is not null)
        {
            try
            {
                if (!_llamaServerProcess.HasExited)
                {
                    _llamaServerProcess.Kill(entireProcessTree: true);
                    _llamaServerProcess.WaitForExit(5000);
                }
            }
            catch
            {
            }
            finally
            {
                _llamaServerProcess.Dispose();
                _llamaServerProcess = null;
                _activeLlamaModel = OfflineTranslationModel.None;
                _activeLlamaServerPath = null;
            }
        }
    }

    private static string GetOfflineModelLabel(OfflineTranslationModel model)
    {
        return model switch
        {
            OfflineTranslationModel.HyMt15_1_8B => "HY-MT1.5 1.8B",
            OfflineTranslationModel.HyMt15_7B => "HY-MT1.5 7B",
            _ => "local translation"
        };
    }

    private void PublishLocalRuntimeStatus(string stage, string message)
    {
        OnLocalRuntimeStatus?.Invoke(new LocalTranslationRuntimeStatus
        {
            Stage = stage,
            Message = message
        });
    }

    private static async Task<IReadOnlyList<string>> TranslateWithOpenAiBatchAsync(
        CloudTranslationOptions options,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(options.Model) ? "gpt-5-mini" : options.Model,
            store = false,
            input = new object[]
            {
                new
                {
                    role = "system",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "Translate each subtitle item into natural English. Return only a JSON array of strings in the same order as the input. Preserve line breaks inside each item. Do not add commentary or markdown."
                        }
                    }
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = JsonSerializer.Serialize(texts)
                        }
                    }
                }
            }
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI translation failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!TryGetOutputText(document.RootElement, out var outputText))
        {
            throw new InvalidOperationException("OpenAI translation returned no output text.");
        }

        try
        {
            var translated = JsonSerializer.Deserialize<List<string>>(outputText);
            if (translated is null || translated.Count != texts.Count)
            {
                throw new InvalidOperationException("OpenAI translation returned an unexpected batch size.");
            }

            return translated;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("OpenAI translation returned malformed JSON.", ex);
        }
    }

    private static async Task<IReadOnlyList<string>> TranslateWithGoogleBatchAsync(
        CloudTranslationOptions options,
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var uri = $"https://translation.googleapis.com/language/translate/v2?key={Uri.EscapeDataString(options.ApiKey)}";
        var formPairs = new List<KeyValuePair<string, string>>
        {
            new("target", targetLanguage),
            new("format", "text")
        };

        formPairs.AddRange(texts.Select(text => new KeyValuePair<string, string>("q", text)));

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(formPairs)
        };

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Translate failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var translations = document.RootElement
            .GetProperty("data")
            .GetProperty("translations")
            .EnumerateArray()
            .Select(item => WebUtility.HtmlDecode(item.GetProperty("translatedText").GetString() ?? string.Empty))
            .ToList();

        if (translations.Count != texts.Count)
        {
            throw new InvalidOperationException("Google Translate returned an unexpected batch size.");
        }

        return translations;
    }

    private static async Task<IReadOnlyList<string>> TranslateWithDeepLBatchAsync(
        CloudTranslationOptions options,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var baseUri = options.ApiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";

        var formPairs = new List<KeyValuePair<string, string>>
        {
            new("target_lang", "EN")
        };

        formPairs.AddRange(texts.Select(text => new KeyValuePair<string, string>("text", text)));

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUri)
        {
            Content = new FormUrlEncodedContent(formPairs)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", options.ApiKey);

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DeepL translation failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var translations = document.RootElement
            .GetProperty("translations")
            .EnumerateArray()
            .Select(item => item.GetProperty("text").GetString() ?? string.Empty)
            .ToList();

        if (translations.Count != texts.Count)
        {
            throw new InvalidOperationException("DeepL translation returned an unexpected batch size.");
        }

        return translations;
    }

    private static async Task<IReadOnlyList<string>> TranslateWithMicrosoftBatchAsync(
        CloudTranslationOptions options,
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Region))
        {
            throw new InvalidOperationException("Microsoft Translator requires a region.");
        }

        var uri = $"https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to={Uri.EscapeDataString(targetLanguage)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Ocp-Apim-Subscription-Key", options.ApiKey);
        request.Headers.Add("Ocp-Apim-Subscription-Region", options.Region.Trim());

        var body = texts.Select(text => new Dictionary<string, string> { ["Text"] = text }).ToList();
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Microsoft Translator failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var translations = document.RootElement
            .EnumerateArray()
            .Select(item => item.GetProperty("translations")[0].GetProperty("text").GetString() ?? string.Empty)
            .ToList();

        if (translations.Count != texts.Count)
        {
            throw new InvalidOperationException("Microsoft Translator returned an unexpected batch size.");
        }

        return translations;
    }

    private static bool TryGetOutputText(JsonElement root, out string text)
    {
        text = string.Empty;

        if (root.TryGetProperty("output_text", out var outputTextElement))
        {
            var outputText = outputTextElement.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(outputText))
            {
                text = outputText;
                return true;
            }
        }

        if (!root.TryGetProperty("output", out var outputArray) || outputArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var output in outputArray.EnumerateArray())
        {
            if (!output.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var content in contentArray.EnumerateArray())
            {
                if (!content.TryGetProperty("text", out var textElement))
                {
                    continue;
                }

                var value = textElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    text = value;
                    return true;
                }
            }
        }

        return false;
    }
}
