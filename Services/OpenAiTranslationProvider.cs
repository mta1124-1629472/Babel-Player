using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public sealed class OpenAiTranslationProvider : ITranslationProvider
{
    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppLog _log;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly Func<OpenAiApiClient> _clientFactory;

    public OpenAiTranslationProvider(
        AppLog log,
        string apiKey,
        string model,
        Func<OpenAiApiClient>? clientFactory = null)
    {
        _log = log;
        _apiKey = apiKey;
        _model = model;
        _clientFactory = clientFactory ?? (() => new OpenAiApiClient(_apiKey));
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.TranscriptJsonPath))
            throw new FileNotFoundException($"Transcript file not found: {request.TranscriptJsonPath}");

        var transcriptArtifact = await ArtifactJson.LoadTranscriptAsync(request.TranscriptJsonPath, cancellationToken);
        var inputArtifact = CreatePromptArtifact(transcriptArtifact, request.SourceLanguage, request.TargetLanguage);

        _log.Info($"[OpenAITranslation] Translating {request.SourceLanguage} -> {request.TargetLanguage} with model '{request.ModelName}'.");

        var translatedArtifact = await RequestTranslatedArtifactAsync(inputArtifact, request.ModelName, cancellationToken);
        await WriteTranslationArtifactAsync(translatedArtifact, request.OutputJsonPath, cancellationToken);

        _log.Info($"[OpenAITranslation] Complete: {translatedArtifact.Segments?.Count ?? 0} segments.");

        return BuildTranslationResult(translatedArtifact);
    }

    public async Task<TranslationResult> TranslateSingleSegmentAsync(
        SingleSegmentTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SourceText))
            throw new ArgumentException("Source text cannot be empty", nameof(request));
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");

        var singleSegmentArtifact = new TranslationArtifact
        {
            SourceLanguage = request.SourceLanguage,
            TargetLanguage = request.TargetLanguage,
            Segments =
            [
                new TranslationSegmentArtifact
                {
                    Id = request.SegmentId,
                    Start = 0.0,
                    End = 0.0,
                    Text = request.SourceText,
                    TranslatedText = string.Empty,
                }
            ],
        };

        _log.Info($"[OpenAITranslation] Single-segment regen: {request.SegmentId}");

        var translatedSingleSegment = await RequestTranslatedArtifactAsync(singleSegmentArtifact, request.ModelName, cancellationToken);
        var translatedText = translatedSingleSegment.Segments?[0].TranslatedText ?? string.Empty;

        var existing = await ArtifactJson.LoadTranslationAsync(request.TranslationJsonPath, cancellationToken);
        var updated = false;
        foreach (var segment in existing.Segments ?? [])
        {
            if (segment.Id == request.SegmentId)
            {
                segment.TranslatedText = translatedText;
                updated = true;
                break;
            }
        }

        if (!updated)
            throw new InvalidOperationException($"Segment '{request.SegmentId}' not found in translation JSON.");

        await WriteTranslationArtifactAsync(existing, request.OutputJsonPath, cancellationToken);
        _log.Info($"[OpenAITranslation] Single-segment regen complete: {request.SegmentId}");

        return BuildTranslationResult(existing);
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new ProviderReadiness(false, "API key missing for provider 'OpenAI API'.");

        return ProviderReadiness.Ready;
    }

    private async Task<TranslationArtifact> RequestTranslatedArtifactAsync(
        TranslationArtifact inputArtifact,
        string model,
        CancellationToken cancellationToken)
    {
        using var client = _clientFactory();

        var responseContent = await client.CreateChatCompletionAsync(
            model,
            BuildSystemPrompt(),
            BuildUserPrompt(inputArtifact),
            cancellationToken);

        var cleanedJson = StripMarkdownCodeFences(responseContent);
        var outputArtifact = ArtifactJson.DeserializeTranslation(cleanedJson, "OpenAI chat completion response");
        ValidateTranslatedArtifact(inputArtifact, outputArtifact);

        outputArtifact.SourceLanguage = inputArtifact.SourceLanguage;
        outputArtifact.TargetLanguage = inputArtifact.TargetLanguage;

        return outputArtifact;
    }

    private static string BuildSystemPrompt() =>
        "You are a translation engine for Babel Player. " +
        "Return only raw JSON with no markdown fences, explanations, or extra keys. " +
        "You will receive a JSON object with sourceLanguage, targetLanguage, and segments. " +
        "For each segment, preserve id, start, end, and text exactly as provided. " +
        "Fill translatedText with a natural spoken-language translation suitable for dubbing. " +
        "Keep segment count and order unchanged. " +
        "If a source text is empty, translatedText must be an empty string.";

    private static string BuildUserPrompt(TranslationArtifact inputArtifact)
    {
        var json = JsonSerializer.Serialize(inputArtifact, PromptJsonOptions);
        return $"Translate this payload and return only the completed JSON object:{Environment.NewLine}{json}";
    }

    private static TranslationArtifact CreatePromptArtifact(
        TranscriptArtifact transcriptArtifact,
        string sourceLanguage,
        string targetLanguage)
    {
        var segments = new List<TranslationSegmentArtifact>();
        foreach (var segment in transcriptArtifact.Segments ?? [])
        {
            segments.Add(new TranslationSegmentArtifact
            {
                Id = SessionWorkflowCoordinator.SegmentId(segment.Start),
                Start = segment.Start,
                End = segment.End,
                Text = segment.Text ?? string.Empty,
                TranslatedText = string.Empty,
            });
        }

        return new TranslationArtifact
        {
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            Segments = segments,
        };
    }

    private static void ValidateTranslatedArtifact(TranslationArtifact inputArtifact, TranslationArtifact outputArtifact)
    {
        var inputSegments = inputArtifact.Segments ?? [];
        var outputSegments = outputArtifact.Segments ?? [];
        if (inputSegments.Count != outputSegments.Count)
        {
            throw new InvalidOperationException(
                $"OpenAI translation artifact segment count mismatch: expected {inputSegments.Count}, got {outputSegments.Count}.");
        }

        for (var index = 0; index < inputSegments.Count; index++)
        {
            var expected = inputSegments[index];
            var actual = outputSegments[index];

            if (!string.Equals(expected.Id, actual.Id, StringComparison.Ordinal))
                throw new InvalidOperationException($"OpenAI translation changed segment id at index {index}.");
            if (!string.Equals(expected.Text, actual.Text, StringComparison.Ordinal))
                throw new InvalidOperationException($"OpenAI translation changed source text for segment '{expected.Id}'.");
            if (Math.Abs(expected.Start - actual.Start) > 0.0001 || Math.Abs(expected.End - actual.End) > 0.0001)
            {
                throw new InvalidOperationException(
                    $"OpenAI translation changed timing for segment '{expected.Id}'.");
            }
        }
    }

    private static TranslationResult BuildTranslationResult(TranslationArtifact artifact)
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