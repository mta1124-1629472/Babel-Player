using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BabelPlayer.Core.Translation;

/// <summary>
/// Offline translation via a locally running llama-server (llama.cpp).
/// Manages the server process lifecycle: starts on first use, reuses while
/// healthy, kills and restarts on model/path change.
/// </summary>
public sealed class LlamaCppTranslationProvider : ITranslationProvider, IDisposable
{
    private const string BaseUrl = "http://127.0.0.1:8097";

    private static readonly HttpClient Http = new();
    private static readonly SemaphoreSlim ServerLock = new(1, 1);

    // shared across all provider instances (only one llama-server runs at a time)
    private static Process?             _serverProcess;
    private static OfflineTranslationModel _activeModel  = OfflineTranslationModel.None;
    private static string?              _activeServerPath;

    private readonly OfflineTranslationModel _model;
    private readonly string                  _serverPath;
    private readonly IBabelLogger            _logger;

    public event Action<LocalTranslationRuntimeStatus>? OnRuntimeStatus;

    public LlamaCppTranslationProvider(
        OfflineTranslationModel model,
        string serverPath,
        IBabelLogFactory? logFactory = null)
    {
        if (model == OfflineTranslationModel.None)
            throw new ArgumentException("Model must not be None.", nameof(model));
        ArgumentException.ThrowIfNullOrWhiteSpace(serverPath);

        _model      = model;
        _serverPath = serverPath.Trim();
        _logger     = (logFactory ?? NullBabelLogFactory.Instance).CreateLogger("translation.llama");
    }

    public string Name => $"llama.cpp ({GetModelLabel(_model)})";

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        // Warm-up doubles as validation — if the server fails to start it throws.
        await EnsureServerAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        await EnsureServerAsync(cancellationToken);

        var results = new List<string>(texts.Count);
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await TranslateOneAsync(text, cancellationToken));
        }
        return results;
    }

    public void Dispose() => StopServer();

    // ── Server lifecycle ───────────────────────────────────────────────────

    private async Task EnsureServerAsync(CancellationToken cancellationToken)
    {
        await ServerLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_serverPath) && !File.Exists(_serverPath))
                throw new InvalidOperationException($"llama-server binary not found at '{_serverPath}'. Set it from Translation > Set llama.cpp Server Path.");

            if (_serverProcess is not null
                && !_serverProcess.HasExited
                && _activeModel == _model
                && string.Equals(_activeServerPath, _serverPath, StringComparison.OrdinalIgnoreCase)
                && await IsServerReadyAsync(cancellationToken))
            {
                _logger.LogInfo("llama-server already running and ready.",
                    BabelLogContext.Create(("model", _model), ("pid", _serverProcess.Id)));
                return;
            }

            StopServer();
            Publish("launching", $"Launching {GetModelLabel(_model)} runtime...");

            var startInfo = new ProcessStartInfo
            {
                FileName               = _serverPath,
                Arguments              = GetServerArguments(_model),
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                WorkingDirectory       = Path.GetDirectoryName(_serverPath) ?? Environment.CurrentDirectory
            };

            _logger.LogInfo("Launching llama-server.",
                BabelLogContext.Create(("model", _model), ("serverPath", _serverPath), ("args", startInfo.Arguments)));

            _serverProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start llama-server.");
            _serverProcess.EnableRaisingEvents = true;
            _serverProcess.Exited += (_, _) =>
                _logger.LogInfo("llama-server exited.",
                    BabelLogContext.Create(("model", _activeModel), ("exitCode", _serverProcess?.ExitCode)));

            _activeModel      = _model;
            _activeServerPath = _serverPath;

            DrainStream(_serverProcess.StandardError,  BabelLogLevel.Warning, "stderr");
            DrainStream(_serverProcess.StandardOutput, BabelLogLevel.Info,    "stdout");

            _logger.LogInfo("llama-server started.",
                BabelLogContext.Create(("model", _model), ("pid", _serverProcess.Id)));

            Publish("downloading-model", $"Downloading {GetModelLabel(_model)} model on first use...");
            await WaitForReadyAsync(cancellationToken);
            Publish("ready", $"{GetModelLabel(_model)} is ready.");

            _logger.LogInfo("llama-server ready.",
                BabelLogContext.Create(("model", _model), ("pid", _serverProcess.Id)));
        }
        finally
        {
            ServerLock.Release();
        }
    }

    private async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        var started         = DateTime.UtcNow;
        var loadingPublished = false;

        while (DateTime.UtcNow - started < TimeSpan.FromMinutes(10))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_serverProcess?.HasExited == true)
            {
                var err = await _serverProcess.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException($"llama-server exited before becoming ready. {err}".Trim());
            }

            if (!loadingPublished && DateTime.UtcNow - started > TimeSpan.FromSeconds(12))
            {
                loadingPublished = true;
                Publish("loading-model", $"Loading {GetModelLabel(_model)} model...");
            }

            if (await IsServerReadyAsync(cancellationToken)) return;

            await Task.Delay(1000, cancellationToken);
        }

        throw new InvalidOperationException("Timed out waiting for llama-server to become ready.");
    }

    private static async Task<bool> IsServerReadyAsync(CancellationToken cancellationToken)
    {
        if (_serverProcess is null || _serverProcess.HasExited) return false;

        try
        {
            using var r = await Http.GetAsync($"{BaseUrl}/health", cancellationToken);
            if (r.IsSuccessStatusCode) return true;
        }
        catch { }

        try
        {
            using var r = await Http.GetAsync($"{BaseUrl}/v1/models", cancellationToken);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private void StopServer()
    {
        if (_serverProcess is null) return;
        try
        {
            if (!_serverProcess.HasExited)
            {
                _logger.LogInfo("Stopping llama-server.",
                    BabelLogContext.Create(("pid", _serverProcess.Id), ("model", _activeModel)));
                _serverProcess.Kill(entireProcessTree: true);
                _serverProcess.WaitForExit(5000);
            }
        }
        catch { }
        finally
        {
            _serverProcess.Dispose();
            _serverProcess    = null;
            _activeModel      = OfflineTranslationModel.None;
            _activeServerPath = null;
        }
    }

    // ── Translation ────────────────────────────────────────────────────────────

    private static async Task<string> TranslateOneAsync(string text, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions");

        var payload = new
        {
            model       = "local",
            messages    = new object[] { new { role = "user", content = BuildPrompt(text) } },
            temperature = 0.2,
            top_p       = 0.6,
            max_tokens  = Math.Max(96, Math.Min(768, text.Length * 4)),
            stream      = false
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HY-MT local translation failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");

        using var doc    = JsonDocument.Parse(body);
        var choice       = doc.RootElement.GetProperty("choices")[0];

        if (choice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var ct))
        {
            var v = ct.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }

        if (choice.TryGetProperty("text", out var te))
        {
            var v = te.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }

        throw new InvalidOperationException("HY-MT local translation returned no text.");
    }

    private static string BuildPrompt(string text)
    {
        var lang = LanguageDetector.Detect(text);
        return lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? $"将以下文本翻译为英语，只输出翻译结果，不要添加解释：\n\n{text}"
            : $"Translate the following text into English. Return only the translation with no commentary:\n\n{text}";
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string GetServerArguments(OfflineTranslationModel model) => model switch
    {
        OfflineTranslationModel.HyMt15_1_8B =>
            "--hf-repo tencent/HY-MT1.5-1.8B-GGUF --hf-file HY-MT1.5-1.8B-Q8_0.gguf --host 127.0.0.1 --port 8097 -c 4096",
        OfflineTranslationModel.HyMt15_7B =>
            "--hf-repo tencent/HY-MT1.5-7B-GGUF --hf-file HY-MT1.5-7B-Q4_K_M.gguf --host 127.0.0.1 --port 8097 -c 4096",
        _ => throw new InvalidOperationException("No llama.cpp arguments defined for this model.")
    };

    private static string GetModelLabel(OfflineTranslationModel model) => model switch
    {
        OfflineTranslationModel.HyMt15_1_8B => "HY-MT1.5 1.8B",
        OfflineTranslationModel.HyMt15_7B   => "HY-MT1.5 7B",
        _                                   => "local translation"
    };

    private void Publish(string stage, string message) =>
        OnRuntimeStatus?.Invoke(new LocalTranslationRuntimeStatus { Stage = stage, Message = message });

    private void DrainStream(StreamReader reader, BabelLogLevel level, string stream)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                        _logger.Log(level, line.Trim(), null,
                            BabelLogContext.Create(("stream", stream), ("model", _model)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("llama-server stream logging failed.", ex,
                    BabelLogContext.Create(("stream", stream), ("model", _model)));
            }
        });
    }
}
