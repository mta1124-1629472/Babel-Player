using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Babel.Player.Models;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

internal static class ArtifactIntegrityValidator
{
    public static bool ValidateMedia(WorkflowSessionSnapshot snapshot, out string? error)
    {
        if (string.IsNullOrWhiteSpace(snapshot.IngestedMediaPath))
        {
            error = "Ingested media path was missing.";
            return false;
        }

        return ValidateFileArtifact(
            snapshot.IngestedMediaPath,
            "media_copy",
            expectedSchemaVersion: null,
            expectedSegmentCount: null,
            expectedSegmentIds: null,
            expectedTiming: null,
            expectedUpstreamHashes: null,
            expectedProvenanceDigest: null,
            out error);
    }

    public static bool ValidateStemPair(WorkflowSessionSnapshot snapshot, out string? error)
    {
        var hasVocals = !string.IsNullOrWhiteSpace(snapshot.VocalsAudioPath);
        var hasAmbiance = !string.IsNullOrWhiteSpace(snapshot.AmbianceAudioPath);
        if (!hasVocals && !hasAmbiance)
        {
            error = null;
            return true;
        }

        if (!hasVocals || !hasAmbiance)
        {
            error = "Vocal separation artifacts were incomplete.";
            return false;
        }

        if (!ValidateMedia(snapshot, out error))
            return false;

        var mediaManifest = ArtifactIntegrity.LoadManifest(snapshot.IngestedMediaPath!);
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(("media_copy", snapshot.IngestedMediaPath));

        if (!ValidateFileArtifact(
                snapshot.VocalsAudioPath!,
                "vocals_stem",
                expectedSchemaVersion: null,
                expectedSegmentCount: null,
                expectedSegmentIds: null,
                expectedTiming: null,
                expectedUpstreamHashes: upstream,
                expectedProvenanceDigest: null,
                out error))
        {
            return false;
        }

        if (!ValidateFileArtifact(
                snapshot.AmbianceAudioPath!,
                "ambiance_stem",
                expectedSchemaVersion: null,
                expectedSegmentCount: null,
                expectedSegmentIds: null,
                expectedTiming: null,
                expectedUpstreamHashes: upstream,
                expectedProvenanceDigest: null,
                out error))
        {
            return false;
        }

        var vocalsManifest = ArtifactIntegrity.LoadManifest(snapshot.VocalsAudioPath!);
        if (!ArtifactIntegrity.DurationsMatch(vocalsManifest.ProbedDurationSeconds, mediaManifest.ProbedDurationSeconds))
        {
            error = "Vocals stem duration did not match the ingested media duration.";
            return false;
        }

        var ambianceManifest = ArtifactIntegrity.LoadManifest(snapshot.AmbianceAudioPath!);
        if (!ArtifactIntegrity.DurationsMatch(ambianceManifest.ProbedDurationSeconds, mediaManifest.ProbedDurationSeconds))
        {
            error = "Ambiance stem duration did not match the ingested media duration.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool ValidateTranscript(WorkflowSessionSnapshot snapshot, out string? error)
    {
        if (!ValidateMedia(snapshot, out error))
            return false;
        if (!ValidateStemPair(snapshot, out error))
            return false;
        if (string.IsNullOrWhiteSpace(snapshot.TranscriptPath))
        {
            error = "Transcript path was missing.";
            return false;
        }

        if (!File.Exists(snapshot.TranscriptPath))
        {
            error = $"Transcript file was missing: {snapshot.TranscriptPath}";
            return false;
        }

        var transcript = ArtifactJson.DeserializeTranscript(File.ReadAllText(snapshot.TranscriptPath), snapshot.TranscriptPath);
        var expectedTiming = ArtifactIntegrity.BuildTranscriptTimingSummary(transcript.Segments);
        var expectedSegmentIds = ArtifactIntegrity.BuildTranscriptSegmentIds(transcript.Segments);
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(
            ("media_copy", snapshot.IngestedMediaPath),
            ("vocals_stem", snapshot.VocalsAudioPath));
        var mediaDuration = ArtifactIntegrity.LoadManifest(snapshot.IngestedMediaPath!).ProbedDurationSeconds;
        var provenance = ArtifactIntegrity.ComputeTranscriptionProvenanceDigest(
            upstream.TryGetValue("media_copy", out var mediaHash) ? mediaHash : null,
            !string.IsNullOrWhiteSpace(snapshot.VocalsAudioPath),
            BuildTranscriptionSettings(snapshot));

        if (!ValidateFileArtifact(
                snapshot.TranscriptPath,
                "transcript",
                transcript.SchemaVersion,
                transcript.Segments?.Count ?? 0,
                expectedSegmentIds,
                expectedTiming,
                upstream,
                provenance,
                out error))
        {
            return false;
        }

        var transcriptManifest = ArtifactIntegrity.LoadManifest(snapshot.TranscriptPath);
        if (!ArtifactIntegrity.DurationsMatch(
                transcriptManifest.SegmentTiming?.DurationSeconds,
                mediaDuration))
        {
            error = "Transcript timing range did not match the expected media duration.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool ValidateTranslation(WorkflowSessionSnapshot snapshot, out string? error)
    {
        if (!ValidateTranscript(snapshot, out error))
            return false;
        if (string.IsNullOrWhiteSpace(snapshot.TranslationPath))
        {
            error = "Translation path was missing.";
            return false;
        }

        var translation = ArtifactJson.DeserializeTranslation(File.ReadAllText(snapshot.TranslationPath), snapshot.TranslationPath);
        var transcript = ArtifactJson.DeserializeTranscript(File.ReadAllText(snapshot.TranscriptPath!), snapshot.TranscriptPath!);
        var transcriptManifest = ArtifactIntegrity.LoadManifest(snapshot.TranscriptPath!);
        var expectedTiming = ArtifactIntegrity.BuildTranslationTimingSummary(translation.Segments);
        var expectedSegmentIds = ArtifactIntegrity.BuildTranslationSegmentIds(translation.Segments);
        var transcriptSegmentIds = ArtifactIntegrity.BuildTranscriptSegmentIds(transcript.Segments);
        if (!ArtifactIntegrity.SegmentIdsMatch(expectedSegmentIds, transcriptSegmentIds))
        {
            error = "Translation segment ids did not match transcript segment ids.";
            return false;
        }

        if ((translation.Segments?.Count ?? 0) != (transcript.Segments?.Count ?? 0))
        {
            error = "Translation segment count did not match transcript segment count.";
            return false;
        }

        var upstream = ArtifactIntegrity.BuildUpstreamHashes(("transcript", snapshot.TranscriptPath));
        var provenance = ArtifactIntegrity.ComputeTranslationProvenanceDigest(
            upstream.TryGetValue("transcript", out var transcriptHash) ? transcriptHash : null,
            BuildTranslationSettings(snapshot),
            translation.SourceLanguage ?? snapshot.SourceLanguage ?? string.Empty,
            translation.TargetLanguage ?? snapshot.TargetLanguage ?? string.Empty);

        if (!ValidateFileArtifact(
                snapshot.TranslationPath,
                "translation",
                translation.SchemaVersion,
                translation.Segments?.Count ?? 0,
                expectedSegmentIds,
                expectedTiming,
                upstream,
                provenance,
                out error))
        {
            return false;
        }

        var translationManifest = ArtifactIntegrity.LoadManifest(snapshot.TranslationPath);
        if (!ArtifactIntegrity.SegmentTimingMatches(
                translationManifest.SegmentTiming,
                transcriptManifest.SegmentTiming))
        {
            error = "Translation timing summary did not match transcript timing summary.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool ValidateTts(WorkflowSessionSnapshot snapshot, out string? error)
    {
        if (!ValidateTranslation(snapshot, out error))
            return false;
        if (string.IsNullOrWhiteSpace(snapshot.TtsSegmentsPath) || string.IsNullOrWhiteSpace(snapshot.TtsPath))
        {
            error = "TTS artifact paths were incomplete.";
            return false;
        }

        if (snapshot.TtsSegmentAudioPaths is not { Count: > 0 })
        {
            error = "TTS segment audio map was missing.";
            return false;
        }

        var translation = ArtifactJson.DeserializeTranslation(File.ReadAllText(snapshot.TranslationPath!), snapshot.TranslationPath!);
        var expectedSegmentIds = ArtifactIntegrity.BuildTranslationSegmentIds(translation.Segments);
        if (snapshot.TtsSegmentAudioPaths.Count != expectedSegmentIds.Count)
        {
            error = "TTS segment audio count did not match translation segment count.";
            return false;
        }

        foreach (var segmentId in expectedSegmentIds)
        {
            if (string.IsNullOrWhiteSpace(segmentId)
                || !snapshot.TtsSegmentAudioPaths.TryGetValue(segmentId, out var segmentPath)
                || string.IsNullOrWhiteSpace(segmentPath)
                || !File.Exists(segmentPath)
                || new FileInfo(segmentPath).Length <= 0)
            {
                error = $"Validated TTS clip was missing for segment '{segmentId}'.";
                return false;
            }
        }

        if (!ValidateDirectoryArtifact(
                snapshot.TtsSegmentsPath,
                "tts_segment_set",
                expectedSegmentIds,
                ArtifactIntegrity.BuildTranslationTimingSummary(translation.Segments),
                ArtifactIntegrity.BuildUpstreamHashes(("translation", snapshot.TranslationPath)),
                ArtifactIntegrity.ComputeTtsSegmentSetProvenanceDigest(
                    ArtifactIntegrity.LoadManifest(snapshot.TranslationPath!).Sha256,
                    snapshot,
                    BuildTtsSettings(snapshot)),
                snapshot.TtsSegmentAudioPaths,
                out error))
        {
            return false;
        }

        var segmentManifest = ArtifactIntegrity.LoadManifest(snapshot.TtsSegmentsPath);
        var translationManifest = ArtifactIntegrity.LoadManifest(snapshot.TranslationPath!);
        var dubProvenance = ArtifactIntegrity.ComputeDubProvenanceDigest(
            segmentManifest.Sha256,
            !string.IsNullOrWhiteSpace(snapshot.AmbianceAudioPath)
                ? ArtifactIntegrity.LoadManifest(snapshot.AmbianceAudioPath!).Sha256
                : null,
            BuildTtsSettings(snapshot));

        var upstream = ArtifactIntegrity.BuildUpstreamHashes(
            ("tts_segment_set", snapshot.TtsSegmentsPath),
            ("ambiance_stem", snapshot.AmbianceAudioPath));

        if (!ValidateFileArtifact(
                snapshot.TtsPath,
                "dub_timeline",
                expectedSchemaVersion: null,
                expectedSegmentCount: expectedSegmentIds.Count,
                expectedSegmentIds: expectedSegmentIds,
                expectedTiming: translationManifest.SegmentTiming,
                expectedUpstreamHashes: upstream,
                expectedProvenanceDigest: dubProvenance,
                out error))
        {
            return false;
        }

        var dubManifest = ArtifactIntegrity.LoadManifest(snapshot.TtsPath);
        if (!ArtifactIntegrity.DurationsMatch(
                dubManifest.ProbedDurationSeconds,
                translationManifest.SegmentTiming?.DurationSeconds))
        {
            error = "Dub duration did not match the expected translation range.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.MixedDubAudioPath))
        {
            if (!ValidateFileArtifact(
                    snapshot.MixedDubAudioPath,
                    "dub_mixed",
                    expectedSchemaVersion: null,
                    expectedSegmentCount: expectedSegmentIds.Count,
                    expectedSegmentIds: expectedSegmentIds,
                    expectedTiming: translationManifest.SegmentTiming,
                    expectedUpstreamHashes: upstream,
                    expectedProvenanceDigest: dubProvenance,
                    out error))
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool ValidateFileArtifact(
        string artifactPath,
        string expectedArtifactKind,
        string? expectedSchemaVersion,
        int? expectedSegmentCount,
        IReadOnlyList<string>? expectedSegmentIds,
        ArtifactSegmentTimingSummary? expectedTiming,
        IReadOnlyDictionary<string, string>? expectedUpstreamHashes,
        string? expectedProvenanceDigest,
        out string? error)
    {
        if (!File.Exists(artifactPath))
        {
            error = $"Artifact file was missing: {artifactPath}";
            return false;
        }

        var info = new FileInfo(artifactPath);
        var actualHash = ArtifactIntegrity.ComputeFileSha256(artifactPath);
        if (!ArtifactIntegrity.TryLoadManifest(artifactPath, out var manifest, out error) || manifest is null)
            return false;

        if (!ArtifactIntegrity.ValidateManifestEnvelope(manifest, expectedArtifactKind, info.Length, actualHash, out error))
            return false;

        if (!string.IsNullOrWhiteSpace(expectedSchemaVersion)
            && !string.Equals(manifest.ArtifactSchemaVersion, expectedSchemaVersion, StringComparison.Ordinal))
        {
            error = $"Manifest schema version '{manifest.ArtifactSchemaVersion ?? "<null>"}' did not match expected schema '{expectedSchemaVersion}'.";
            return false;
        }

        if (expectedSegmentCount.HasValue && manifest.SegmentCount != expectedSegmentCount.Value)
        {
            error = $"Manifest segment count {manifest.SegmentCount?.ToString() ?? "<null>"} did not match expected count {expectedSegmentCount.Value}.";
            return false;
        }

        if (expectedSegmentIds is not null
            && !ArtifactIntegrity.SegmentIdsMatch(expectedSegmentIds, manifest.SegmentIds))
        {
            error = "Manifest segment ids did not match expected segment ids.";
            return false;
        }

        if (expectedTiming is not null
            && !ArtifactIntegrity.SegmentTimingMatches(manifest.SegmentTiming, expectedTiming))
        {
            error = "Manifest segment timing summary did not match expected timing summary.";
            return false;
        }

        if (expectedUpstreamHashes is not null)
        {
            foreach (var pair in expectedUpstreamHashes)
            {
                if (manifest.UpstreamArtifactHashes is null
                    || !manifest.UpstreamArtifactHashes.TryGetValue(pair.Key, out var manifestHash)
                    || !string.Equals(manifestHash, pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Manifest upstream hash for '{pair.Key}' was missing or mismatched.";
                    return false;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedProvenanceDigest)
            && !string.Equals(manifest.ProvenanceDigest, expectedProvenanceDigest, StringComparison.OrdinalIgnoreCase))
        {
            error = "Manifest provenance digest did not match the current session snapshot.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool ValidateDirectoryArtifact(
        string artifactPath,
        string expectedArtifactKind,
        IReadOnlyList<string> expectedSegmentIds,
        ArtifactSegmentTimingSummary? expectedTiming,
        IReadOnlyDictionary<string, string>? expectedUpstreamHashes,
        string expectedProvenanceDigest,
        IReadOnlyDictionary<string, string> segmentAudioPaths,
        out string? error)
    {
        if (!Directory.Exists(artifactPath))
        {
            error = $"Artifact directory was missing: {artifactPath}";
            return false;
        }

        if (!ArtifactIntegrity.TryLoadManifest(artifactPath, out var manifest, out error) || manifest is null)
            return false;

        if (!string.Equals(manifest.ManifestVersion, ArtifactIntegrity.ManifestVersion, StringComparison.Ordinal))
        {
            error = $"Unsupported manifest version '{manifest.ManifestVersion ?? "<null>"}'.";
            return false;
        }

        if (!string.Equals(manifest.ArtifactKind, expectedArtifactKind, StringComparison.Ordinal))
        {
            error = $"Manifest artifact kind '{manifest.ArtifactKind ?? "<null>"}' did not match expected kind '{expectedArtifactKind}'.";
            return false;
        }

        var orderedHashes = new List<string>(expectedSegmentIds.Count * 3);
        long totalSize = 0;
        foreach (var segmentId in expectedSegmentIds)
        {
            if (!segmentAudioPaths.TryGetValue(segmentId, out var segmentPath) || !File.Exists(segmentPath))
            {
                error = $"TTS segment file for '{segmentId}' was missing.";
                return false;
            }

            var info = new FileInfo(segmentPath);
            if (info.Length <= 0)
            {
                error = $"TTS segment file for '{segmentId}' was empty.";
                return false;
            }

            var hash = ArtifactIntegrity.ComputeFileSha256(segmentPath);
            orderedHashes.Add(segmentId);
            orderedHashes.Add(hash);
            orderedHashes.Add(info.Length.ToString());
            totalSize += info.Length;
        }

        if (manifest.FileSizeBytes != totalSize || totalSize <= 0)
        {
            error = "Manifest segment-set file size did not match the generated segment clips.";
            return false;
        }

        var actualHash = ArtifactIntegrity.ComputeCompositeSha256(orderedHashes);
        if (!string.Equals(manifest.Sha256, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            error = "Manifest segment-set hash did not match the generated segment clips.";
            return false;
        }

        if (manifest.SegmentCount != expectedSegmentIds.Count)
        {
            error = $"Manifest segment count {manifest.SegmentCount?.ToString() ?? "<null>"} did not match expected count {expectedSegmentIds.Count}.";
            return false;
        }

        if (!ArtifactIntegrity.SegmentIdsMatch(expectedSegmentIds, manifest.SegmentIds))
        {
            error = "Manifest segment ids did not match expected segment ids.";
            return false;
        }

        if (!ArtifactIntegrity.SegmentTimingMatches(manifest.SegmentTiming, expectedTiming))
        {
            error = "Manifest segment timing summary did not match expected translation timing.";
            return false;
        }

        if (expectedUpstreamHashes is not null)
        {
            foreach (var pair in expectedUpstreamHashes)
            {
                if (manifest.UpstreamArtifactHashes is null
                    || !manifest.UpstreamArtifactHashes.TryGetValue(pair.Key, out var manifestHash)
                    || !string.Equals(manifestHash, pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Manifest upstream hash for '{pair.Key}' was missing or mismatched.";
                    return false;
                }
            }
        }

        if (!string.Equals(manifest.ProvenanceDigest, expectedProvenanceDigest, StringComparison.OrdinalIgnoreCase))
        {
            error = "Manifest provenance digest did not match the current session snapshot.";
            return false;
        }

        error = null;
        return true;
    }

    private static AppSettings BuildTranscriptionSettings(WorkflowSessionSnapshot snapshot) =>
        new()
        {
            TranscriptionRuntime = snapshot.TranscriptionRuntime ?? InferenceRuntimeCatalog.InferTranscriptionRuntime(snapshot.TranscriptionProvider),
            TranscriptionProvider = snapshot.TranscriptionProvider ?? string.Empty,
            TranscriptionModel = snapshot.TranscriptionModel ?? string.Empty,
            TranscriptionLanguageHint = snapshot.TranscriptionLanguageHint,
        };

    private static AppSettings BuildTranslationSettings(WorkflowSessionSnapshot snapshot) =>
        new()
        {
            TranslationRuntime = snapshot.TranslationRuntime ?? InferenceRuntimeCatalog.InferTranslationRuntime(snapshot.TranslationProvider),
            TranslationProvider = snapshot.TranslationProvider ?? string.Empty,
            TranslationModel = snapshot.TranslationModel ?? string.Empty,
        };

    private static AppSettings BuildTtsSettings(WorkflowSessionSnapshot snapshot) =>
        new()
        {
            TtsRuntime = snapshot.TtsRuntime ?? InferenceRuntimeCatalog.InferTtsRuntime(snapshot.TtsProvider),
            TtsProvider = snapshot.TtsProvider ?? string.Empty,
            TtsVoice = snapshot.TtsVoice ?? string.Empty,
            DubTimingMode = snapshot.DubTimingMode ?? SegmentTimingMode.Off,
            AmbianceMixDb = snapshot.AmbianceMixDb ?? -15.0,
        };
}
