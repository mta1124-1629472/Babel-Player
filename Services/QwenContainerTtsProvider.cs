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
public sealed class QwenContainerTtsProvider(
    ContainerizedInferenceClient client,
    AppLog log,
    TtsReferenceExtractor extractor) : ITtsProvider, IAsyncDisposable
{
    private readonly ContainerizedInferenceClient _client = client;
    private readonly AppLog _log = log;
    private readonly TtsReferenceExtractor _extractor = extractor;

    private string? _autoExtractedReferencePath;
    private readonly Dictionary<string, string> _referenceIdCache = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
        /// Determines whether the containerized TTS provider is ready based on the given application settings.
        /// </summary>
        /// <param name="settings">Application settings used to evaluate TTS readiness.</param>
        /// <param name="keyStore">Optional API key store used during readiness checks.</param>
        /// <returns>A <see cref="ProviderReadiness"/> value indicating the provider's readiness status and any required configuration.</returns>
        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
        ContainerizedProviderReadiness.CheckTts(settings);

    /// <summary>
    /// Synthesizes speech for a single segment using Qwen3-TTS and saves the resulting audio to the requested output path.
    /// </summary>
    /// <param name="request">Parameters for segment synthesis. Must include non-empty Text and either ReferenceAudioPath or SourceVideoPath; OutputAudioPath is where the synthesized audio will be written.</param>
    /// <param name="cancellationToken">Token to observe while awaiting asynchronous operations.</param>
    /// <returns>A <see cref="TtsResult"/> describing the synthesis outcome; its <c>AudioPath</c> is the final local output path specified by <paramref name="request"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <see cref="SingleSegmentTtsRequest.Text"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no reference audio is available or when the TTS service reports a failure.</exception>
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

    /// <summary>
    /// Generates combined TTS audio for the provided TtsRequest and produces a single output audio result.
    /// </summary>
    /// <returns>The TtsResult describing the produced output audio path and related metadata.</returns>
    /// <exception cref="NotImplementedException">Always thrown: combined generation is now handled by the coordinator.</exception>
    public Task<TtsResult> GenerateTtsAsync(
        TtsRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("PLACEHOLDER: Combined generation is now handled by the coordinator.");
    }

    /// <summary>
    /// Reset the provider's session state.
    /// </summary>
    /// <remarks>
    /// Clears the in-memory reference ID cache and, if an auto-extracted reference audio path is present,
    /// deletes that extracted file via the configured extractor and clears the cached path.
    /// </remarks>
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

    private static readonly HashSet<string> ValidQwenModelNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Qwen/Qwen3-TTS-12Hz-0.6B-Base",
            "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
        };

    private static string ResolveModel(string? voiceName) =>
        !string.IsNullOrWhiteSpace(voiceName) && ValidQwenModelNames.Contains(voiceName.Trim())
            ? voiceName.Trim()
            : "Qwen/Qwen3-TTS-12Hz-1.7B-Base";

    /// <summary>
    /// Sanitizes a string for use as a file name component by replacing characters invalid in file names with underscores.
    /// </summary>
    /// <param name="value">The input string to sanitize.</param>
    /// <returns>The input string with every character invalid in file names replaced by '_' so it can be safely used as a file name component.</returns>
    private static string SanitizeFileComponent(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }


}
