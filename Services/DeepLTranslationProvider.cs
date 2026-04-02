using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public sealed class DeepLTranslationProvider : ITranslationProvider
{
    private readonly AppLog _log;
    private readonly string _apiKey;
    private readonly Func<DeepLApiClient> _clientFactory;

    public DeepLTranslationProvider(
        AppLog log,
        string apiKey,
        Func<DeepLApiClient>? clientFactory = null)
    {
        _log = log;
        _apiKey = apiKey;
        _clientFactory = clientFactory ?? (() => new DeepLApiClient(_apiKey));
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.TranscriptJsonPath))
            throw new FileNotFoundException($"Transcript file not found: {request.TranscriptJsonPath}");

        var transcriptArtifact = await ArtifactJson.LoadTranscriptAsync(request.TranscriptJsonPath, cancellationToken);
        var inputSegments = transcriptArtifact.Segments ?? [];

        using var client = _clientFactory();
        var translations = await client.TranslateTextsAsync(
            [.. inputSegments.Select(segment => segment.Text ?? string.Empty)],
            request.TargetLanguage,
            request.SourceLanguage,
            cancellationToken);

        if (translations.Count != inputSegments.Count)
        {
            throw new InvalidOperationException(
                $"DeepL translation segment count mismatch: expected {inputSegments.Count}, got {translations.Count}.");
        }

        var translationArtifact = new TranslationArtifact
        {
            SourceLanguage = ResolveSourceLanguage(request.SourceLanguage, translations),
            TargetLanguage = request.TargetLanguage,
            Segments =
            [
                .. inputSegments.Select((segment, index) => new TranslationSegmentArtifact
                {
                    Id = SessionWorkflowCoordinator.SegmentId(segment.Start),
                    Start = segment.Start,
                    End = segment.End,
                    Text = segment.Text ?? string.Empty,
                    TranslatedText = translations[index].Text,
                })
            ],
        };

        await WriteTranslationArtifactAsync(translationArtifact, request.OutputJsonPath, cancellationToken);
        _log.Info($"[DeepLTranslation] Complete: {translationArtifact.Segments?.Count ?? 0} segments.");

        return BuildResult(translationArtifact);
    }

    public async Task<TranslationResult> TranslateSingleSegmentAsync(
        SingleSegmentTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SourceText))
            throw new ArgumentException("Source text cannot be empty", nameof(request));
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");

        using var client = _clientFactory();
        var translated = await client.TranslateTextsAsync(
            [request.SourceText],
            request.TargetLanguage,
            request.SourceLanguage,
            cancellationToken);

        if (translated.Count == 0)
            throw new InvalidOperationException("DeepL returned no translation for single-segment request.");

        var existing = await ArtifactJson.LoadTranslationAsync(request.TranslationJsonPath, cancellationToken);
        var updated = false;
        foreach (var segment in existing.Segments ?? [])
        {
            if (segment.Id == request.SegmentId)
            {
                segment.TranslatedText = translated[0].Text;
                updated = true;
                break;
            }
        }

        if (!updated)
            throw new InvalidOperationException($"Segment '{request.SegmentId}' not found in translation JSON.");

        await WriteTranslationArtifactAsync(existing, request.OutputJsonPath, cancellationToken);
        _log.Info($"[DeepLTranslation] Single-segment regen complete: {request.SegmentId}");

        return BuildResult(existing);
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new ProviderReadiness(false, "API key missing for provider 'DeepL API'.");

        return ProviderReadiness.Ready;
    }

    private static string ResolveSourceLanguage(string requestedSourceLanguage, IReadOnlyList<DeepLTranslationItem> translations)
    {
        if (!string.IsNullOrWhiteSpace(requestedSourceLanguage)
            && !string.Equals(requestedSourceLanguage, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return requestedSourceLanguage;
        }

        var detected = translations.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.DetectedSourceLanguage))?.DetectedSourceLanguage;
        return string.IsNullOrWhiteSpace(detected)
            ? requestedSourceLanguage
            : detected.ToLowerInvariant();
    }

    private static async Task WriteTranslationArtifactAsync(
        TranslationArtifact artifact,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var artifactDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(artifactDir))
            Directory.CreateDirectory(artifactDir);

        await File.WriteAllTextAsync(outputPath, ArtifactJson.SerializeTranslation(artifact), cancellationToken);
    }

    private static TranslationResult BuildResult(TranslationArtifact artifact)
    {
        var segments = new List<TranslatedSegment>();
        foreach (var segment in artifact.Segments ?? [])
        {
            segments.Add(new TranslatedSegment(
                segment.Start,
                segment.End,
                segment.Text ?? string.Empty,
                segment.TranslatedText ?? string.Empty));
        }

        return new TranslationResult(
            true,
            segments,
            artifact.SourceLanguage ?? string.Empty,
            artifact.TargetLanguage ?? string.Empty,
            null);
    }
}