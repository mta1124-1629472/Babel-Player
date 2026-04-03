using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Controls whether Gemini transcribes in the source language only,
/// or transcribes AND translates to the target language in one pass.
/// When set to TranscribeAndTranslate, the SessionWorkflowCoordinator
/// should skip the separate translation stage.
/// </summary>
public enum GeminiTranscribeMode
{
    TranscribeOnly,
    TranscribeAndTranslate,
}

public sealed class GeminiTranscriptionProvider : ITranscriptionProvider
{
    private readonly AppLog _log;
    private readonly string _apiKey;
    private readonly Func<GeminiApiClient> _clientFactory;

    /// <summary>
    /// When TranscribeAndTranslate, the provider builds a prompt that asks Gemini
    /// to output the transcript already translated into the target language.
    /// The caller is responsible for reading this property and bypassing the
    /// translation stage if it is set to TranscribeAndTranslate.
    /// </summary>
    public GeminiTranscribeMode TranscribeMode { get; }

    /// <summary>
    /// When TranscribeMode is TranscribeAndTranslate, the target language
    /// code (e.g. "English", "Spanish") to include in the prompt.
    /// </summary>
    public string? TranslateTargetLanguage { get; }

    public GeminiTranscriptionProvider(
        AppLog log,
        string apiKey,
        GeminiTranscribeMode transcribeMode = GeminiTranscribeMode.TranscribeOnly,
        string? translateTargetLanguage = null,
        Func<GeminiApiClient>? clientFactory = null)
    {
        _log = log;
        _apiKey = apiKey;
        TranscribeMode = transcribeMode;
        TranslateTargetLanguage = translateTargetLanguage;
        _clientFactory = clientFactory ?? (() => new GeminiApiClient(_apiKey));
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new ProviderReadiness(false, "API key missing for provider 'Google Gemini'.");

        return ProviderReadiness.Ready;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        _log.Info($"[GeminiTranscription] Transcribing: {request.SourceAudioPath} (model={request.ModelName}, mode={TranscribeMode})");

        var prompt = BuildTranscriptionPrompt(request.LanguageHint);

        using var client = _clientFactory();
        var rawResponse = await client.TranscribeAudioAsync(
            request.SourceAudioPath,
            request.ModelName,
            prompt,
            cancellationToken);

        var segments = ParseSegmentsFromResponse(rawResponse);

        var detectedLanguage = TranscribeMode == GeminiTranscribeMode.TranscribeAndTranslate
            ? (TranslateTargetLanguage ?? request.LanguageHint ?? "unknown")
            : (request.LanguageHint ?? "unknown");

        var transcriptArtifact = new TranscriptArtifact
        {
            Language = detectedLanguage,
            LanguageProbability = 1.0,
            Segments =
            [
                .. System.Linq.Enumerable.Select(segments, s => new TranscriptSegmentArtifact
                {
                    Start = s.StartSeconds,
                    End = s.EndSeconds,
                    Text = s.Text,
                })
            ]
        };

        var outputDir = Path.GetDirectoryName(request.OutputJsonPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllTextAsync(request.OutputJsonPath, ArtifactJson.SerializeTranscript(transcriptArtifact), cancellationToken);

        _log.Info($"[GeminiTranscription] Complete: {segments.Count} segments.");

        return new TranscriptionResult(
            true,
            segments,
            detectedLanguage,
            1.0,
            null);
    }

    private string BuildTranscriptionPrompt(string? languageHint)
    {
        if (TranscribeMode == GeminiTranscribeMode.TranscribeAndTranslate
            && !string.IsNullOrWhiteSpace(TranslateTargetLanguage))
        {
            return
                $"Transcribe this audio and translate it directly into {TranslateTargetLanguage}. " +
                "Return a JSON array of timestamped segments only, with no markdown fences and no extra text. " +
                "Each element must have exactly these fields: start (seconds, number), end (seconds, number), text (string). " +
                "Example: [{\"start\":0.0,\"end\":3.2,\"text\":\"Hello world\"}]";
        }

        var langHint = string.IsNullOrWhiteSpace(languageHint) || string.Equals(languageHint, "auto", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" The audio is in {languageHint}.";

        return
            $"Transcribe this audio verbatim.{langHint} " +
            "Return a JSON array of timestamped segments only, with no markdown fences and no extra text. " +
            "Each element must have exactly these fields: start (seconds, number), end (seconds, number), text (string). " +
            "Example: [{\"start\":0.0,\"end\":3.2,\"text\":\"Hello world\"}]";
    }

    private static List<TranscriptSegment> ParseSegmentsFromResponse(string rawResponse)
    {
        var trimmed = StripMarkdownCodeFences(rawResponse.Trim());

        var segments = new List<TranscriptSegment>();

        try
        {
            var array = JsonNode.Parse(trimmed)?.AsArray();
            if (array is null)
                return FallbackSingleSegment(rawResponse);

            foreach (var node in array)
            {
                if (node is not JsonObject obj) continue;

                var text = obj["text"]?.GetValue<string?>();
                if (string.IsNullOrWhiteSpace(text)) continue;

                var start = obj["start"]?.GetValue<double?>() ?? 0.0;
                var end   = obj["end"]?.GetValue<double?>() ?? start;
                if (end < start) end = start;

                segments.Add(new TranscriptSegment(start, end, text));
            }
        }
        catch
        {
            return FallbackSingleSegment(rawResponse);
        }

        return segments.Count > 0 ? segments : FallbackSingleSegment(rawResponse);
    }

    private static List<TranscriptSegment> FallbackSingleSegment(string text)
    {
        var cleaned = text.Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            return [];
        return [new TranscriptSegment(0.0, 0.0, cleaned)];
    }

    private static string StripMarkdownCodeFences(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return trimmed;

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstNewline)
            return trimmed;

        return trimmed.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
    }
}
