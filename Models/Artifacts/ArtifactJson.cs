using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Models;

public static class ArtifactJson
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    public static TranscriptArtifact DeserializeTranscript(string json, string contextLabel)
    {
        var artifact = JsonSerializer.Deserialize<TranscriptArtifact>(json, ReadOptions)
            ?? throw CreateInvalidArtifactException("transcript", contextLabel, "JSON deserialized to null.");

        ValidateTranscript(artifact, contextLabel);
        return artifact;
    }

    public static TranslationArtifact DeserializeTranslation(string json, string contextLabel)
    {
        var artifact = JsonSerializer.Deserialize<TranslationArtifact>(json, ReadOptions)
            ?? throw CreateInvalidArtifactException("translation", contextLabel, "JSON deserialized to null.");

        ValidateTranslation(artifact, contextLabel);
        return artifact;
    }

    public static async Task<TranscriptArtifact> LoadTranscriptAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return DeserializeTranscript(json, path);
    }

    public static async Task<TranslationArtifact> LoadTranslationAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return DeserializeTranslation(json, path);
    }

    public static string SerializeTranscript(TranscriptArtifact artifact)
    {
        ValidateTranscript(artifact, "serialize");
        return JsonSerializer.Serialize(artifact, WriteOptions);
    }

    public static string SerializeTranslation(TranslationArtifact artifact)
    {
        ValidateTranslation(artifact, "serialize");
        return JsonSerializer.Serialize(artifact, WriteOptions);
    }

    private static void ValidateTranscript(TranscriptArtifact artifact, string contextLabel)
    {
        if (string.IsNullOrWhiteSpace(artifact.Language))
            throw CreateInvalidArtifactException("transcript", contextLabel, "Missing required 'language'.");
        if (artifact.Segments is null)
            throw CreateInvalidArtifactException("transcript", contextLabel, "Missing required 'segments'.");

        for (var i = 0; i < artifact.Segments.Count; i++)
        {
            var segment = artifact.Segments[i];
            if (segment is null)
                throw CreateInvalidArtifactException("transcript", contextLabel, $"Segment {i} is null.");
            if (string.IsNullOrWhiteSpace(segment.Text))
                throw CreateInvalidArtifactException("transcript", contextLabel, $"Segment {i} is missing required 'text'.");
            ValidateSegmentBounds("transcript", contextLabel, i, segment.Start, segment.End);
        }
    }

    private static void ValidateTranslation(TranslationArtifact artifact, string contextLabel)
    {
        if (string.IsNullOrWhiteSpace(artifact.SourceLanguage))
            throw CreateInvalidArtifactException("translation", contextLabel, "Missing required 'sourceLanguage'.");
        if (string.IsNullOrWhiteSpace(artifact.TargetLanguage))
            throw CreateInvalidArtifactException("translation", contextLabel, "Missing required 'targetLanguage'.");
        if (artifact.Segments is null)
            throw CreateInvalidArtifactException("translation", contextLabel, "Missing required 'segments'.");

        for (var i = 0; i < artifact.Segments.Count; i++)
        {
            var segment = artifact.Segments[i];
            if (segment is null)
                throw CreateInvalidArtifactException("translation", contextLabel, $"Segment {i} is null.");
            if (string.IsNullOrWhiteSpace(segment.Id))
                throw CreateInvalidArtifactException("translation", contextLabel, $"Segment {i} is missing required 'id'.");
            if (segment.Text is null)
                throw CreateInvalidArtifactException("translation", contextLabel, $"Segment {i} is missing required 'text'.");
            if (segment.TranslatedText is null)
                throw CreateInvalidArtifactException("translation", contextLabel, $"Segment {i} is missing required 'translatedText'.");
            ValidateSegmentBounds("translation", contextLabel, i, segment.Start, segment.End);
        }
    }

    private static void ValidateSegmentBounds(string artifactKind, string contextLabel, int index, double start, double end)
    {
        if (double.IsNaN(start) || double.IsInfinity(start))
            throw CreateInvalidArtifactException(artifactKind, contextLabel, $"Segment {index} has invalid 'start'.");
        if (double.IsNaN(end) || double.IsInfinity(end))
            throw CreateInvalidArtifactException(artifactKind, contextLabel, $"Segment {index} has invalid 'end'.");
        if (end < start)
            throw CreateInvalidArtifactException(artifactKind, contextLabel, $"Segment {index} has end < start.");
    }

    private static InvalidOperationException CreateInvalidArtifactException(
        string artifactKind,
        string contextLabel,
        string reason) =>
        new($"Invalid {artifactKind} artifact '{contextLabel}': {reason}");
}
