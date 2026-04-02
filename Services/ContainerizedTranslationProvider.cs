using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

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
        var transcriptArtifact = ArtifactJson.DeserializeTranscript(transcriptJson, request.TranscriptJsonPath);

        var result = await _client.TranslateAsync(
            transcriptJson,
            request.SourceLanguage,
            request.TargetLanguage,
            cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException(
                $"Containerized translation failed: {result.ErrorMessage}");
        if (result.Segments.Count != transcriptArtifact.Segments?.Count)
        {
            throw new InvalidOperationException(
                $"Containerized translation artifact segment count mismatch: expected {transcriptArtifact.Segments?.Count ?? 0}, got {result.Segments.Count}.");
        }

        var translationArtifact = CreateTranslationArtifact(
            result.Segments,
            result.SourceLanguage,
            result.TargetLanguage);

        await WriteTranslationArtifactAsync(translationArtifact, request.OutputJsonPath, cancellationToken);

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
        var singleSegmentTranscript = ArtifactJson.SerializeTranscript(new TranscriptArtifact
        {
            Language = request.SourceLanguage,
            LanguageProbability = 1.0,
            Segments =
            [
                new TranscriptSegmentArtifact
                {
                    Start = 0.0,
                    End = 0.0,
                    Text = request.SourceText,
                }
            ],
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

        var existing = await ArtifactJson.LoadTranslationAsync(request.TranslationJsonPath, cancellationToken);

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

        await WriteTranslationArtifactAsync(existing, request.OutputJsonPath, cancellationToken);

        _log.Info($"[ContainerizedTranslation] Single-segment regen complete: {request.SegmentId}");

        return updatedResult;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task WriteTranslationArtifactAsync(
        TranslationArtifact artifact,
        string outputPath,
        CancellationToken ct)
    {
        var artifactDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(artifactDir))
            Directory.CreateDirectory(artifactDir);

        await File.WriteAllTextAsync(outputPath,
            ArtifactJson.SerializeTranslation(artifact), ct);
    }

    private static IReadOnlyList<TranslatedSegment> BuildTranslatedSegments(
        IEnumerable<TranslationSegmentArtifact> helpers)
    {
        var result = new List<TranslatedSegment>();
        foreach (var h in helpers)
            result.Add(new TranslatedSegment(h.Start, h.End, h.Text ?? "", h.TranslatedText ?? ""));
        return result;
    }

    private static TranslationArtifact CreateTranslationArtifact(
        IReadOnlyList<TranslatedSegment> segments,
        string sourceLanguage,
        string targetLanguage)
    {
        var artifact = new TranslationArtifact
        {
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            Segments =
            [
                .. System.Linq.Enumerable.Select(segments, s => new TranslationSegmentArtifact
                {
                    Id = SessionWorkflowCoordinator.SegmentId(s.StartSeconds),
                    Start = s.StartSeconds,
                    End = s.EndSeconds,
                    Text = s.Text,
                    TranslatedText = s.TranslatedText,
                })
            ],
        };

        return artifact;
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        return ContainerizedProviderReadiness.CheckTranslation(settings);
    }
}
