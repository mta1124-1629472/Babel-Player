using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.Services.Translations;

namespace Babel.Player.Services;

/// <summary>
/// Translation provider backed by the containerized inference service (<c>/translate</c>).
/// Bridges the file-path-based <see cref="ITranslationProvider"/> contract to the HTTP client.
/// Passes the raw transcript JSON artifact directly to the service (no re-serialization).
/// Writes a translation JSON artifact to <see cref="TranslationRequest.OutputJsonPath"/> in
/// the same format produced by <see cref="GoogleTranslationProvider"/>.
/// </summary>
public sealed class ContainerizedTranslationProvider : ITranslationProvider
{
    private readonly ContainerizedInferenceClient _client;
    private readonly AppLog _log;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOptions  = new() { PropertyNameCaseInsensitive = true };

    public ContainerizedTranslationProvider(ContainerizedInferenceClient client, AppLog log)
    {
        _client = client;
        _log = log;
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.TranscriptJsonPath))
            throw new FileNotFoundException($"Transcript file not found: {request.TranscriptJsonPath}");

        _log.Info($"[ContainerizedTranslation] Translating {request.SourceLanguage} -> {request.TargetLanguage}");

        var transcriptJson = await File.ReadAllTextAsync(request.TranscriptJsonPath, cancellationToken);

        var result = await _client.TranslateAsync(
            transcriptJson,
            request.SourceLanguage,
            request.TargetLanguage,
            cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException(
                $"Containerized translation failed: {result.ErrorMessage}");

        await WriteTranslationArtifactAsync(
            result.Segments, result.SourceLanguage, result.TargetLanguage,
            request.OutputJsonPath, cancellationToken);

        _log.Info($"[ContainerizedTranslation] Complete: {result.Segments.Count} segments");

        return result;
    }

    public async Task<TranslationResult> TranslateSingleSegmentAsync(
        SingleSegmentTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        _log.Info($"[ContainerizedTranslation] Single-segment regen: {request.SegmentId}");

        // Send a minimal transcript with just this one segment so the server endpoint is reused.
        // Start/end are not meaningful for translation; we only need the translated text.
        var singleSegmentTranscript = JsonSerializer.Serialize(new
        {
            segments = new[] { new { start = 0.0, end = 0.0, text = request.SourceText } },
        });

        var result = await _client.TranslateAsync(
            singleSegmentTranscript,
            request.SourceLanguage,
            request.TargetLanguage,
            cancellationToken);

        if (!result.Success || result.Segments.Count == 0)
            throw new InvalidOperationException(
                $"Containerized single-segment translation failed: {result.ErrorMessage}");

        var translatedText = result.Segments[0].TranslatedText;

        // Read existing translation JSON, update the target segment, write back.
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");

        var existing = TranslationJsonHelper.Parse(
            await File.ReadAllTextAsync(request.TranslationJsonPath, cancellationToken));
        if (existing == null)
            throw new InvalidOperationException($"Failed to parse translation JSON: {request.TranslationJsonPath}");

        var updated = false;
        if (existing.Segments != null)
        {
            foreach (var seg in existing.Segments)
            {
                if (seg.Id == request.SegmentId)
                {
                    seg.TranslatedText = translatedText;
                    updated = true;
                    break;
                }
            }
        }

        if (!updated)
            throw new InvalidOperationException(
                $"Segment '{request.SegmentId}' not found in translation JSON.");

        var sourceLanguage = existing.SourceLanguage ?? request.SourceLanguage;
        var targetLanguage = existing.TargetLanguage ?? request.TargetLanguage;

        var updatedSegments = BuildTranslatedSegments(existing.Segments ?? []);
        var updatedResult = new TranslationResult(
            true, updatedSegments, sourceLanguage, targetLanguage, null);

        await WriteTranslationArtifactFromHelperAsync(
            existing, request.OutputJsonPath, cancellationToken);

        _log.Info($"[ContainerizedTranslation] Single-segment regen complete: {request.SegmentId}");

        return updatedResult;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task WriteTranslationArtifactAsync(
        IReadOnlyList<TranslatedSegment> segments,
        string sourceLanguage,
        string targetLanguage,
        string outputPath,
        CancellationToken ct)
    {
        var artifactDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(artifactDir))
            Directory.CreateDirectory(artifactDir);

        var artifact = new
        {
            sourceLanguage,
            targetLanguage,
            segments = System.Linq.Enumerable.Select(segments, s => new
            {
                id            = SessionWorkflowCoordinator.SegmentId(s.StartSeconds),
                start         = s.StartSeconds,
                end           = s.EndSeconds,
                text          = s.Text,
                translatedText = s.TranslatedText,
            }),
        };

        await File.WriteAllTextAsync(outputPath,
            JsonSerializer.Serialize(artifact, WriteOptions), ct);
    }

    private static async Task WriteTranslationArtifactFromHelperAsync(
        TranslationJsonHelper helper,
        string outputPath,
        CancellationToken ct)
    {
        var artifactDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(artifactDir))
            Directory.CreateDirectory(artifactDir);

        await File.WriteAllTextAsync(outputPath,
            JsonSerializer.Serialize(helper, WriteOptions), ct);
    }

    private static IReadOnlyList<TranslatedSegment> BuildTranslatedSegments(
        IEnumerable<TranslationJsonHelper.SegmentJsonHelper> helpers)
    {
        var result = new List<TranslatedSegment>();
        foreach (var h in helpers)
            result.Add(new TranslatedSegment(h.Start, h.End, h.Text ?? "", h.TranslatedText ?? ""));
        return result;
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (string.IsNullOrWhiteSpace(settings.ContainerizedServiceUrl))
            return new ProviderReadiness(false, "No containerized service URL configured in Settings.");
        return ProviderReadiness.Ready;
    }
}
