using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// TTS provider backed by the containerized inference service (<c>/tts</c> + <c>/tts/audio/{filename}</c>).
/// Bridges the file-path-based <see cref="ITtsProvider"/> contract to the HTTP client.
/// The service writes audio to its own temp directory; this adapter downloads the result
/// via <c>GET /tts/audio/{filename}</c> and writes it to the locally requested output path.
/// </summary>
public sealed class ContainerizedTtsProvider : ITtsProvider
{
    private readonly ContainerizedInferenceClient _client;
    private readonly AppLog _log;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public ContainerizedTtsProvider(ContainerizedInferenceClient client, AppLog log)
    {
        _client = client;
        _log = log;
    }

    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        _log.Info($"[ContainerizedTts] Generating segment TTS (voice: {request.VoiceName})");

        var result = await _client.TextToSpeechAsync(
            request.Text,
            request.VoiceName,
            cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException($"Containerized TTS failed: {result.ErrorMessage}");

        await DownloadToOutputPathAsync(result.AudioPath, request.OutputAudioPath, cancellationToken);

        _log.Info($"[ContainerizedTts] Segment TTS saved to: {request.OutputAudioPath}");

        return result with { AudioPath = request.OutputAudioPath };
    }

    public async Task<TtsResult> GenerateTtsAsync(
        TtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");

        _log.Info($"[ContainerizedTts] Generating combined TTS (voice: {request.VoiceName})");

        // Read all translated segments and concatenate their text for combined output.
        var json = await File.ReadAllTextAsync(request.TranslationJsonPath, cancellationToken);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        var segments = doc.GetProperty("segments");

        var parts = new System.Collections.Generic.List<string>();
        foreach (var seg in segments.EnumerateArray())
        {
            var textProp = seg.GetProperty("translatedText");
            var text = textProp.ValueKind == JsonValueKind.String ? textProp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(text!);
        }

        var combined = string.Join(" ", parts);

        var result = await _client.TextToSpeechAsync(
            combined,
            request.VoiceName,
            cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException($"Containerized TTS (combined) failed: {result.ErrorMessage}");

        await DownloadToOutputPathAsync(result.AudioPath, request.OutputAudioPath, cancellationToken);

        _log.Info($"[ContainerizedTts] Combined TTS saved to: {request.OutputAudioPath}");

        return result with { AudioPath = request.OutputAudioPath };
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task DownloadToOutputPathAsync(
        string serverAudioPath,
        string localOutputPath,
        CancellationToken ct)
    {
        var filename = Path.GetFileName(serverAudioPath);
        if (string.IsNullOrEmpty(filename))
            throw new InvalidOperationException(
                $"Cannot extract filename from server audio path: '{serverAudioPath}'");

        var outputDir = Path.GetDirectoryName(localOutputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await _client.DownloadTtsAudioAsync(filename, localOutputPath, ct);
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (string.IsNullOrWhiteSpace(settings.ContainerizedServiceUrl))
            return new ProviderReadiness(false, "No containerized service URL configured in Settings.");
        return ProviderReadiness.Ready;
    }
}
