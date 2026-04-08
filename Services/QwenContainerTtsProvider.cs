using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// TTS provider backed by Qwen3-TTS containerized endpoints.
/// Uses per-segment reference audio for voice cloning; no separate reference-registration step.
/// </summary>
public sealed class QwenContainerTtsProvider : ITtsProvider, IAsyncDisposable
{
    private readonly ContainerizedInferenceClient _client;
    private readonly AppLog _log;
    private readonly TtsReferenceExtractor _extractor;
    private readonly Func<IReadOnlyList<string>, string, CancellationToken, Task> _combineAudioFunc;

    private string? _autoExtractedReferencePath;
    private readonly Dictionary<string, string> _referenceIdCache = new(StringComparer.Ordinal);
    private bool _disposed;

    public QwenContainerTtsProvider(
        ContainerizedInferenceClient client,
        AppLog log,
        TtsReferenceExtractor extractor,
        Func<IReadOnlyList<string>, string, CancellationToken, Task>? combineAudioFunc = null)
    {
        _client = client;
        _log = log;
        _extractor = extractor;
        _combineAudioFunc = combineAudioFunc ?? CombineAudioSegmentsAsync;
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
        ContainerizedProviderReadiness.CheckTts(settings);

    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Segment text cannot be empty", nameof(request));

        _log.Info($"[QwenContainerTts] Segment synth start");

        var referenceAudioPath = request.ReferenceAudioPath;
        if (string.IsNullOrWhiteSpace(referenceAudioPath) && !string.IsNullOrWhiteSpace(request.SourceVideoPath))
            referenceAudioPath = await EnsureAutoExtractedReferenceAsync(request.SourceVideoPath, cancellationToken);

        if (string.IsNullOrWhiteSpace(referenceAudioPath))
            throw new InvalidOperationException(
                "Qwen3-TTS requires a speaker reference audio clip. Provide ReferenceAudioPath or SourceVideoPath.");

        var language = ResolveLanguage(request.Language);
        var model = ResolveModel(request.VoiceName);

        var result = await QwenSegmentWithRetryAsync(
            request.Text, model, language,
            referenceAudioPath,
            request.SpeakerId ?? QwenReferenceKeys.SingleSpeakerDefault,
            request.ReferenceTranscriptText,
            cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException($"Qwen3-TTS failed: {result.ErrorMessage}");

        await DownloadToOutputPathAsync(result.AudioPath, request.OutputAudioPath, cancellationToken);

        _log.Info($"[QwenContainerTts] Segment synth saved: {request.OutputAudioPath}");
        return result with { AudioPath = request.OutputAudioPath };
    }

    public async Task<TtsResult> GenerateTtsAsync(
        TtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");

        var translation = await ArtifactJson.LoadTranslationAsync(request.TranslationJsonPath, cancellationToken);
        var validSegments = (translation.Segments ?? [])
            .Where(seg => !string.IsNullOrWhiteSpace(seg.TranslatedText))
            .ToList();
        var totalSegments = validSegments.Count;
        var segmentAudioPaths = new List<string>();
        var workDir = Path.Combine(
            Path.GetTempPath(),
            $"babel-qwen-{Path.GetFileNameWithoutExtension(request.OutputAudioPath)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var language = ResolveLanguage(request.Language ?? translation.TargetLanguage);

        try
        {
            request.SegmentProgress?.Report((0, totalSegments));
            var segmentIndex = 0;
            foreach (var seg in validSegments)
            {
                segmentIndex++;
                var resolvedModel = ResolveModel(ResolveVoiceForSegment(seg, request));
                var referenceAudioPath = ResolveReferenceAudioForSegment(seg, request);

                if (string.IsNullOrWhiteSpace(referenceAudioPath) &&
                    !string.IsNullOrWhiteSpace(request.SourceVideoPath))
                {
                    referenceAudioPath = await EnsureAutoExtractedReferenceAsync(request.SourceVideoPath, cancellationToken);
                }

                if (string.IsNullOrWhiteSpace(referenceAudioPath))
                    throw new InvalidOperationException(
                        $"Qwen3-TTS requires a reference clip for speaker '{seg.SpeakerId ?? "default"}'.");

                _log.Info(
                    $"[QwenContainerTts] Combined synth segment start " +
                    $"(segment={seg.Id ?? segmentIndex.ToString()}, model={resolvedModel}, " +
                    $"reference={Path.GetFileName(referenceAudioPath)})");

                var result = await QwenSegmentWithRetryAsync(
                    seg.TranslatedText!, resolvedModel, language,
                    referenceAudioPath,
                    seg.SpeakerId ?? QwenReferenceKeys.SingleSpeakerDefault,
                    seg.Text,
                    cancellationToken);

                if (!result.Success)
                    throw new InvalidOperationException($"Qwen3-TTS combined synthesis failed: {result.ErrorMessage}");

                var segmentAudioPath = Path.Combine(
                    workDir,
                    $"{segmentIndex:D4}_{SanitizeFileComponent(seg.Id ?? $"segment_{segmentIndex}")}.mp3");

                await DownloadToOutputPathAsync(result.AudioPath, segmentAudioPath, cancellationToken);
                segmentAudioPaths.Add(segmentAudioPath);
                request.SegmentProgress?.Report((segmentIndex, totalSegments));
            }

            if (segmentAudioPaths.Count == 0)
                throw new InvalidOperationException("No translated text found for Qwen3-TTS synthesis.");

            var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
                Directory.CreateDirectory(outputDir);

            if (segmentAudioPaths.Count == 1)
                File.Copy(segmentAudioPaths[0], request.OutputAudioPath, overwrite: true);
            else
                await _combineAudioFunc(segmentAudioPaths, request.OutputAudioPath, cancellationToken);

            _log.Info($"[QwenContainerTts] Combined synth saved: {request.OutputAudioPath}");
            return new TtsResult(
                true,
                request.OutputAudioPath,
                ResolveModel(request.VoiceName),
                new FileInfo(request.OutputAudioPath).Length,
                null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    public async Task ResetSessionAsync()
    {
        _log.Info("[QwenContainerTts] Resetting session state");
        _referenceIdCache.Clear();
        if (!string.IsNullOrWhiteSpace(_autoExtractedReferencePath))
        {
            await _extractor.DeleteAsync();
            _autoExtractedReferencePath = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await ResetSessionAsync();
        await _extractor.DisposeAsync();
        _disposed = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string?> EnsureReferenceRegisteredAsync(
        string referenceAudioPath,
        string speakerId,
        CancellationToken ct)
    {
        var cacheKey = $"{speakerId}|{referenceAudioPath}";
        if (_referenceIdCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var refId = await _client.RegisterQwenReferenceAsync(speakerId, referenceAudioPath, ct);
        _referenceIdCache[cacheKey] = refId;
        _log.Info($"[QwenContainerTts] Registered reference for speaker '{speakerId}': {refId}");
        return refId;
    }

    /// <summary>
    /// Synthesizes one segment using a registered reference ID. If the server returns an
    /// error consistent with a stale/evicted reference (e.g. after a server restart),
    /// evicts the cache entry, re-registers once, and retries.
    /// </summary>
    private async Task<TtsResult> QwenSegmentWithRetryAsync(
        string text,
        string model,
        string language,
        string referenceAudioPath,
        string speakerId,
        string? referenceText,
        CancellationToken ct)
    {
        var refId = await EnsureReferenceRegisteredAsync(referenceAudioPath, speakerId, ct);
        var result = await _client.QwenSegmentAsync(
            text, model, language, referenceId: refId, referenceText: referenceText, cancellationToken: ct);

        if (!result.Success && IsLikelyStaleReferenceError(result.ErrorMessage))
        {
            _log.Warning($"[QwenContainerTts] Stale reference_id for speaker '{speakerId}', re-registering.");
            _referenceIdCache.Remove($"{speakerId}|{referenceAudioPath}");
            refId = await EnsureReferenceRegisteredAsync(referenceAudioPath, speakerId, ct);
            result = await _client.QwenSegmentAsync(
                text, model, language, referenceId: refId, referenceText: referenceText, cancellationToken: ct);
        }

        return result;
    }

    private static bool IsLikelyStaleReferenceError(string? errorMessage) =>
        errorMessage is not null && (
            errorMessage.Contains("reference_id", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase));

    private async Task<string?> EnsureAutoExtractedReferenceAsync(string sourceVideoPath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_autoExtractedReferencePath))
            return _autoExtractedReferencePath;

        _log.Info($"[QwenContainerTts] Auto-extracting reference audio from: {sourceVideoPath}");
        _autoExtractedReferencePath = await _extractor.ExtractReferenceAsync(sourceVideoPath, ct);
        return _autoExtractedReferencePath;
    }

    private async Task DownloadToOutputPathAsync(string serverAudioPath, string localOutputPath, CancellationToken ct)
    {
        var filename = Path.GetFileName(serverAudioPath);
        if (string.IsNullOrWhiteSpace(filename))
            throw new InvalidOperationException($"Cannot extract filename from server audio path: '{serverAudioPath}'");

        var outputDir = Path.GetDirectoryName(localOutputPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
            Directory.CreateDirectory(outputDir);

        await _client.DownloadTtsAudioAsync(filename, localOutputPath, ct);
    }

    private static string ResolveVoiceForSegment(TranslationSegmentArtifact segment, TtsRequest request)
    {
        if (!string.IsNullOrWhiteSpace(segment.SpeakerId)
            && request.SpeakerVoiceAssignments is not null
            && request.SpeakerVoiceAssignments.TryGetValue(segment.SpeakerId!, out var mappedVoice)
            && !string.IsNullOrWhiteSpace(mappedVoice))
        {
            return mappedVoice;
        }

        return !string.IsNullOrWhiteSpace(request.DefaultVoiceFallback)
            ? request.DefaultVoiceFallback
            : request.VoiceName;
    }

    private static string? ResolveReferenceAudioForSegment(TranslationSegmentArtifact segment, TtsRequest request)
    {
        if (request.SpeakerReferenceAudioPaths is null)
            return null;

        if (!string.IsNullOrWhiteSpace(segment.SpeakerId)
            && request.SpeakerReferenceAudioPaths.TryGetValue(segment.SpeakerId!, out var referencePath)
            && !string.IsNullOrWhiteSpace(referencePath))
        {
            return referencePath;
        }

        return request.SpeakerReferenceAudioPaths.TryGetValue(QwenReferenceKeys.SingleSpeakerDefault, out var defaultReferencePath)
               && !string.IsNullOrWhiteSpace(defaultReferencePath)
            ? defaultReferencePath
            : null;
    }

    // qwen-tts 0.1.1 expects full English language names, not BCP-47 codes.
    // Supported: auto, chinese, english, french, german, italian, japanese, korean, portuguese, russian, spanish
    private static string ResolveLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "english";

        var normalized = language.Trim().ToLowerInvariant();
        // Strip region suffix first (e.g. "zh-cn" → "zh", "pt-br" → "pt")
        var code = normalized.Contains('-') ? normalized.Split('-', 2)[0] : normalized;
        return code switch
        {
            "en" => "english",
            "zh" => "chinese",
            "fr" => "french",
            "de" => "german",
            "it" => "italian",
            "ja" => "japanese",
            "ko" => "korean",
            "pt" => "portuguese",
            "ru" => "russian",
            "es" => "spanish",
            // Already a full name (e.g. passed directly) or "auto"
            _ => normalized,
        };
    }

    private static readonly IReadOnlySet<string> ValidQwenModelNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Qwen/Qwen3-TTS-12Hz-0.6B-Base",
            "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
        };

    private static string ResolveModel(string? voiceName) =>
        !string.IsNullOrWhiteSpace(voiceName) && ValidQwenModelNames.Contains(voiceName.Trim())
            ? voiceName.Trim()
            : "Qwen/Qwen3-TTS-12Hz-1.7B-Base";

    private static string SanitizeFileComponent(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    private static string EscapeConcatListPath(string path) =>
        path.Replace("\\", "/", StringComparison.Ordinal)
            .Replace("'", "'\\''", StringComparison.Ordinal);

    private static async Task CombineAudioSegmentsAsync(
        IReadOnlyList<string> segmentAudioPaths,
        string outputAudioPath,
        CancellationToken cancellationToken)
    {
        if (segmentAudioPaths.Count == 0)
            throw new InvalidOperationException("Cannot combine zero Qwen TTS segment audio files.");

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg not found. Qwen3-TTS combined output requires ffmpeg.");
        var concatListDir = Path.Combine(Path.GetTempPath(), $"babel-qwen-concat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(concatListDir);
        var concatListPath = Path.Combine(concatListDir, "inputs.txt");
        var concatFile = string.Join(
            Environment.NewLine,
            segmentAudioPaths.Select(path => $"file '{EscapeConcatListPath(path)}'"));

        await File.WriteAllTextAsync(concatListPath, concatFile, cancellationToken);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("concat");
            psi.ArgumentList.Add("-safe");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(concatListPath);
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("libmp3lame");
            psi.ArgumentList.Add("-q:a");
            psi.ArgumentList.Add("3");
            psi.ArgumentList.Add(outputAudioPath);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffmpeg for Qwen TTS segment concatenation.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg Qwen TTS concatenation failed with exit code {process.ExitCode}: {stderr} {stdout}".Trim());
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(concatListDir))
                    Directory.Delete(concatListDir, recursive: true);
            }
            catch
            {
            }
        }
    }
}
