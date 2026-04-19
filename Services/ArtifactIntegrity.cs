using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

internal static class ArtifactIntegrity
{
    internal const string ManifestVersion = "1.0";
    internal const double DurationToleranceFloorSeconds = 0.25;
    internal const double DurationToleranceRatio = 0.02;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    public static string GetWorkingPath(string finalPath) => $"{finalPath}.work";

    public static string ResolveFinalPath(string path) =>
        path.EndsWith(".work", StringComparison.OrdinalIgnoreCase)
            ? path[..^".work".Length]
            : path;

    public static async Task<string> FinalizeWorkingArtifactAsync(
        string workingOrFinalPath,
        CancellationToken cancellationToken = default)
    {
        var finalPath = ResolveFinalPath(workingOrFinalPath);
        if (string.Equals(workingOrFinalPath, finalPath, StringComparison.Ordinal))
            return finalPath;

        cancellationToken.ThrowIfCancellationRequested();
        ArtifactPersistence.AtomicReplace(workingOrFinalPath, finalPath);
        return finalPath;
    }

    public static ArtifactIntegrityManifest LoadManifest(string artifactPath)
    {
        var manifestPath = ArtifactPersistence.GetManifestPath(artifactPath);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Artifact manifest not found: {manifestPath}", manifestPath);

        var json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<ArtifactIntegrityManifest>(json, ReadOptions)
            ?? throw new InvalidOperationException($"Artifact manifest '{manifestPath}' deserialized to null.");
    }

    public static bool TryLoadManifest(string artifactPath, out ArtifactIntegrityManifest? manifest, out string? error)
    {
        try
        {
            manifest = LoadManifest(artifactPath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            manifest = null;
            error = ex.Message;
            return false;
        }
    }

    public static bool HasManifest(string? artifactPath) =>
        !string.IsNullOrWhiteSpace(artifactPath)
        && File.Exists(ArtifactPersistence.GetManifestPath(artifactPath));

    public static string? TryGetArtifactSha256(string? artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath))
            return null;

        if (TryLoadManifest(artifactPath, out var manifest, out _)
            && !string.IsNullOrWhiteSpace(manifest?.Sha256))
        {
            return manifest.Sha256;
        }

        return File.Exists(artifactPath)
            ? ComputeFileSha256(artifactPath)
            : null;
    }

    public static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public static string ComputeStringSha256(string value)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static string ComputeCompositeSha256(IEnumerable<string> values) =>
        ComputeStringSha256(string.Join("\n", values));

    public static double ComputeDurationTolerance(double expectedDurationSeconds) =>
        Math.Max(DurationToleranceFloorSeconds, expectedDurationSeconds * DurationToleranceRatio);

    public static bool DurationsMatch(double? actualDurationSeconds, double? expectedDurationSeconds)
    {
        if (!actualDurationSeconds.HasValue || !expectedDurationSeconds.HasValue)
            return true;

        return Math.Abs(actualDurationSeconds.Value - expectedDurationSeconds.Value)
            <= ComputeDurationTolerance(expectedDurationSeconds.Value);
    }

    public static ArtifactSegmentTimingSummary? BuildTranscriptTimingSummary(IReadOnlyList<TranscriptSegmentArtifact>? segments)
    {
        if (segments is not { Count: > 0 })
            return null;

        var ordered = segments.OrderBy(segment => segment.Start).ToList();
        var start = ordered[0].Start;
        var end = ordered[^1].End;
        return new ArtifactSegmentTimingSummary
        {
            StartSeconds = start,
            EndSeconds = end,
            DurationSeconds = Math.Max(0, end - start),
        };
    }

    public static ArtifactSegmentTimingSummary? BuildTranslationTimingSummary(IReadOnlyList<TranslationSegmentArtifact>? segments)
    {
        if (segments is not { Count: > 0 })
            return null;

        var ordered = segments.OrderBy(segment => segment.Start).ToList();
        var start = ordered[0].Start;
        var end = ordered[^1].End;
        return new ArtifactSegmentTimingSummary
        {
            StartSeconds = start,
            EndSeconds = end,
            DurationSeconds = Math.Max(0, end - start),
        };
    }

    public static List<string> BuildTranscriptSegmentIds(IReadOnlyList<TranscriptSegmentArtifact>? segments) =>
        segments is null
            ? []
            : [.. segments.Select(segment => SessionWorkflowCoordinator.SegmentId(segment.Start))];

    public static List<string> BuildTranslationSegmentIds(IReadOnlyList<TranslationSegmentArtifact>? segments) =>
        segments is null
            ? []
            : [.. segments.Select(segment => segment.Id ?? string.Empty)];

    public static Dictionary<string, string> BuildUpstreamHashes(params (string Key, string? ArtifactPath)[] entries)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, artifactPath) in entries)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(artifactPath))
                continue;

            var hash = TryGetArtifactSha256(artifactPath);
            if (!string.IsNullOrWhiteSpace(hash))
                result[key] = hash;
        }

        return result;
    }

    public static async Task<ArtifactIntegrityManifest> WriteFileManifestAsync(
        string artifactPath,
        string artifactKind,
        string? artifactSchemaVersion,
        double? probedDurationSeconds,
        int? segmentCount,
        IReadOnlyList<string>? segmentIds,
        ArtifactSegmentTimingSummary? segmentTiming,
        IDictionary<string, string>? upstreamArtifactHashes,
        string provenanceDigest,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(artifactPath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"Artifact not found for manifest generation: {artifactPath}", artifactPath);

        var manifest = new ArtifactIntegrityManifest
        {
            ManifestVersion = ManifestVersion,
            ArtifactKind = artifactKind,
            ArtifactSchemaVersion = artifactSchemaVersion,
            Sha256 = ComputeFileSha256(artifactPath),
            FileSizeBytes = fileInfo.Length,
            ProbedDurationSeconds = probedDurationSeconds,
            SegmentCount = segmentCount,
            SegmentIds = segmentIds is null ? null : [.. segmentIds],
            SegmentTiming = segmentTiming,
            UpstreamArtifactHashes = upstreamArtifactHashes is null
                ? null
                : new Dictionary<string, string>(upstreamArtifactHashes, StringComparer.Ordinal),
            ProvenanceDigest = provenanceDigest,
        };

        await ArtifactPersistence.WriteManifestAsync(artifactPath, manifest, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    public static async Task<ArtifactIntegrityManifest> WriteDirectoryManifestAsync(
        string artifactPath,
        string artifactKind,
        IReadOnlyList<KeyValuePair<string, string>> orderedArtifacts,
        double? probedDurationSeconds,
        ArtifactSegmentTimingSummary? segmentTiming,
        IDictionary<string, string>? upstreamArtifactHashes,
        string provenanceDigest,
        CancellationToken cancellationToken = default)
    {
        var combinedSize = 0L;
        var inputs = new List<string>(orderedArtifacts.Count * 3);
        var ids = new List<string>(orderedArtifacts.Count);

        foreach (var pair in orderedArtifacts)
        {
            var info = new FileInfo(pair.Value);
            if (!info.Exists)
                throw new FileNotFoundException($"Segment artifact not found for manifest generation: {pair.Value}", pair.Value);

            var hash = ComputeFileSha256(pair.Value);
            combinedSize += info.Length;
            ids.Add(pair.Key);
            inputs.Add(pair.Key);
            inputs.Add(hash);
            inputs.Add(info.Length.ToString(CultureInfo.InvariantCulture));
        }

        var manifest = new ArtifactIntegrityManifest
        {
            ManifestVersion = ManifestVersion,
            ArtifactKind = artifactKind,
            ArtifactSchemaVersion = null,
            Sha256 = ComputeCompositeSha256(inputs),
            FileSizeBytes = combinedSize,
            ProbedDurationSeconds = probedDurationSeconds,
            SegmentCount = orderedArtifacts.Count,
            SegmentIds = ids,
            SegmentTiming = segmentTiming,
            UpstreamArtifactHashes = upstreamArtifactHashes is null
                ? null
                : new Dictionary<string, string>(upstreamArtifactHashes, StringComparer.Ordinal),
            ProvenanceDigest = provenanceDigest,
        };

        await ArtifactPersistence.WriteManifestAsync(artifactPath, manifest, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    public static string ComputeTranscriptionProvenanceDigest(
        string? sourceMediaHash,
        bool vocalSeparationEnabled,
        AppSettings settings) =>
        ComputeCompositeSha256(
        [
            "stage=transcription",
            $"source_media={sourceMediaHash ?? string.Empty}",
            $"vocal_separation={vocalSeparationEnabled}",
            $"runtime={settings.TranscriptionRuntime}",
            $"provider={settings.TranscriptionProvider ?? string.Empty}",
            $"model={settings.TranscriptionModel ?? string.Empty}",
            $"language_hint={SessionSnapshotSemantics.NormalizeTranscriptionLanguageHint(settings.TranscriptionLanguageHint) ?? "auto"}",
        ]);

    public static string ComputeTranslationProvenanceDigest(
        string? transcriptHash,
        AppSettings settings,
        string sourceLanguage,
        string targetLanguage) =>
        ComputeCompositeSha256(
        [
            "stage=translation",
            $"transcript={transcriptHash ?? string.Empty}",
            $"runtime={settings.TranslationRuntime}",
            $"provider={settings.TranslationProvider ?? string.Empty}",
            $"model={settings.TranslationModel ?? string.Empty}",
            $"source_language={sourceLanguage}",
            $"target_language={targetLanguage}",
        ]);

    public static string ComputeTtsSegmentSetProvenanceDigest(
        string? translationHash,
        WorkflowSessionSnapshot snapshot,
        AppSettings settings) =>
        ComputeCompositeSha256(
        [
            "stage=tts_segment_set",
            $"translation={translationHash ?? string.Empty}",
            $"runtime={settings.TtsRuntime}",
            $"provider={settings.TtsProvider ?? string.Empty}",
            $"voice={snapshot.TtsVoice ?? settings.TtsVoice ?? string.Empty}",
            $"fallback_voice={snapshot.DefaultTtsVoiceFallback ?? string.Empty}",
            $"speaker_voices={SerializeDictionary(snapshot.SpeakerVoiceAssignments)}",
            $"speaker_references={SerializeReferenceDictionary(snapshot.SpeakerReferenceAudioPaths)}",
            $"timing_overrides={SerializeTimingOverrides(snapshot.SegmentTimingModeOverrides)}",
        ]);

    public static string ComputeDubProvenanceDigest(
        string? segmentSetHash,
        string? ambianceStemHash,
        AppSettings settings) =>
        ComputeCompositeSha256(
        [
            "stage=dub_final",
            $"segment_set={segmentSetHash ?? string.Empty}",
            $"dub_timing={DubTimingDefaults.NormalizeRenderTimingMode(settings.DubTimingMode)}",
            $"ambiance_mix_db={settings.AmbianceMixDb.ToString("0.###", CultureInfo.InvariantCulture)}",
            $"ambiance_stem={ambianceStemHash ?? string.Empty}",
        ]);

    public static bool SegmentTimingMatches(ArtifactSegmentTimingSummary? left, ArtifactSegmentTimingSummary? right)
    {
        if (left is null && right is null)
            return true;
        if (left is null || right is null)
            return false;

        return Math.Abs(left.StartSeconds - right.StartSeconds) < 0.000001
            && Math.Abs(left.EndSeconds - right.EndSeconds) < 0.000001
            && Math.Abs(left.DurationSeconds - right.DurationSeconds) < 0.000001;
    }

    public static bool SegmentIdsMatch(IReadOnlyList<string>? expected, IReadOnlyList<string>? actual)
    {
        expected ??= [];
        actual ??= [];
        if (expected.Count != actual.Count)
            return false;

        for (var index = 0; index < expected.Count; index++)
        {
            if (!string.Equals(expected[index], actual[index], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    public static bool ValidateManifestEnvelope(
        ArtifactIntegrityManifest manifest,
        string expectedArtifactKind,
        long actualFileSizeBytes,
        string actualSha256,
        out string? error)
    {
        if (!string.Equals(manifest.ManifestVersion, ManifestVersion, StringComparison.Ordinal))
        {
            error = $"Unsupported manifest version '{manifest.ManifestVersion ?? "<null>"}'.";
            return false;
        }

        if (!string.Equals(manifest.ArtifactKind, expectedArtifactKind, StringComparison.Ordinal))
        {
            error = $"Manifest artifact kind '{manifest.ArtifactKind ?? "<null>"}' did not match expected kind '{expectedArtifactKind}'.";
            return false;
        }

        if (actualFileSizeBytes <= 0)
        {
            error = "Artifact file size was zero.";
            return false;
        }

        if (manifest.FileSizeBytes != actualFileSizeBytes)
        {
            error = $"Manifest file size {manifest.FileSizeBytes} did not match actual size {actualFileSizeBytes}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            error = "Manifest sha256 was missing.";
            return false;
        }

        if (!string.Equals(manifest.Sha256, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            error = "Artifact hash did not match manifest sha256.";
            return false;
        }

        error = null;
        return true;
    }

    private static string SerializeDictionary(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
            return string.Empty;

        return string.Join(
            "|",
            values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static string SerializeReferenceDictionary(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
            return string.Empty;

        var materialized = new List<string>(values.Count);
        foreach (var pair in values.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pair.Value) || !File.Exists(pair.Value))
            {
                materialized.Add($"{pair.Key}=");
                continue;
            }

            materialized.Add($"{pair.Key}={ComputeFileSha256(pair.Value)}");
        }

        return string.Join("|", materialized);
    }

    private static string SerializeTimingOverrides(IReadOnlyDictionary<string, SegmentTimingMode>? values)
    {
        if (values is null || values.Count == 0)
            return string.Empty;

        return string.Join(
            "|",
            values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={(int)pair.Value}"));
    }
}
