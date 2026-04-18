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

        if (!File.Exists(snapshot.IngestedMediaPath))
        {
            error = $"Artifact file was missing: {snapshot.IngestedMediaPath}";
            return false;
        }

        if (!ArtifactIntegrity.HasManifest(snapshot.IngestedMediaPath))
        {
            error = null;
            return true;
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

        var upstream = ArtifactIntegrity.BuildUpstreamHashes(("media_copy", snapshot.IngestedMediaPath));
        var strictVocals = ArtifactIntegrity.HasManifest(snapshot.VocalsAudioPath);
        var strictAmbiance = ArtifactIntegrity.HasManifest(snapshot.AmbianceAudioPath);

        if (!File.Exists(snapshot.VocalsAudioPath!))
        {
            error = $"Artifact file was missing: {snapshot.VocalsAudioPath}";
            return false;
        }

        if (!File.Exists(snapshot.AmbianceAudioPath!))
        {
            error = $"Artifact file was missing: {snapshot.AmbianceAudioPath}";
            return false;
        }

        if (strictVocals
            && !ValidateFileArtifact(
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

        if (strictAmbiance
            && !ValidateFileArtifact(
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

        if (ArtifactIntegrity.TryLoadManifest(snapshot.IngestedMediaPath!, out var mediaManifest, out _)
            && mediaManifest is not null)
        {
            if (ArtifactIntegrity.TryLoadManifest(snapshot.VocalsAudioPath!, out var vocalsManifest, out _)
                && vocalsManifest is not null
                && !ArtifactIntegrity.DurationsMatch(vocalsManifest.ProbedDurationSeconds, mediaManifest.ProbedDurationSeconds))
            {
                error = "Vocals stem duration did not match the ingested media duration.";
                return false;
            }

            if (ArtifactIntegrity.TryLoadManifest(snapshot.AmbianceAudioPath!, out var ambianceManifest, out _)
                && ambianceManifest is not null
                && !ArtifactIntegrity.DurationsMatch(ambianceManifest.ProbedDurationSeconds, mediaManifest.ProbedDurationSeconds))
            {
                error = "Ambiance stem duration did not match the ingested media duration.";
                return false;
            }
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
            error = $"Artifact file was missing: {snapshot.TranscriptPath}";
            return false;
        }

        if (!ArtifactIntegrity.HasManifest(snapshot.TranscriptPath))
        {
            error = null;
            return true;
        }

        var transcript = ArtifactJson.DeserializeTranscript(File.ReadAllText(snapshot.TranscriptPath), snapshot.TranscriptPath);
        var expectedTiming = ArtifactIntegrity.BuildTranscriptTimingSummary(transcript.Segments);
        var expectedSegmentIds = ArtifactIntegrity.BuildTranscriptSegmentIds(transcript.Segments);
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(
            ("media_copy", snapshot.IngestedMediaPath),
            ("vocals_stem", snapshot.VocalsAudioPath));
        var provenance = ArtifactIntegrity.ComputeTranscriptionProvenanceDigest(
            ArtifactIntegrity.TryGetArtifactSha256(snapshot.IngestedMediaPath),
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

        if (ArtifactIntegrity.TryLoadManifest(snapshot.TranscriptPath, out var transcriptManifest, out _)
            && transcriptManifest is not null
            && ArtifactIntegrity.TryLoadManifest(snapshot.IngestedMediaPath!, out var mediaManifest, out _)
            && mediaManifest is not null
            && !ArtifactIntegrity.DurationsMatch(
                transcriptManifest.SegmentTiming?.DurationSeconds,
                mediaManifest.ProbedDurationSeconds))
        {
            error = "Transcript timing range did not match the expected media duration.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool ValidateTranslation(WorkflowSessionSnapshot snapshot, out string? error)
    {
        if (string.IsNullOrWhiteSpace(snapshot.TranslationPath))
        {
            error = "Translation path was missing.";
            return false;
        }

        if (!File.Exists(snapshot.TranslationPath))
        {
            error = $"Artifact file was missing: {snapshot.TranslationPath}";
            return false;
        }

        if (ShouldValidateTranscriptChain(snapshot) && !ValidateTranscript(snapshot, out error))
            return false;

        if (!ArtifactIntegrity.HasManifest(snapshot.TranslationPath))
        {
            error = null;
            return true;
        }

        var translation = ArtifactJson.DeserializeTranslation(File.ReadAllText(snapshot.TranslationPath), snapshot.TranslationPath);
        var expectedTiming = ArtifactIntegrity.BuildTranslationTimingSummary(translation.Segments);
        var expectedSegmentIds = ArtifactIntegrity.BuildTranslationSegmentIds(translation.Segments);
        ArtifactSegmentTimingSummary? transcriptTiming = null;
        if (ArtifactIntegrity.HasManifest(snapshot.TranscriptPath)
            && !string.IsNullOrWhiteSpace(snapshot.TranscriptPath)
            && File.Exists(snapshot.TranscriptPath))
        {
            var transcript = ArtifactJson.DeserializeTranscript(File.ReadAllText(snapshot.TranscriptPath!), snapshot.TranscriptPath!);
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

            transcriptTiming = ArtifactIntegrity.BuildTranscriptTimingSummary(transcript.Segments);
        }

        var upstream = ArtifactIntegrity.BuildUpstreamHashes(("transcript", snapshot.TranscriptPath));
        var provenance = ArtifactIntegrity.ComputeTranslationProvenanceDigest(
            ArtifactIntegrity.TryGetArtifactSha256(snapshot.TranscriptPath),
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

        if (transcriptTiming is not null
            && ArtifactIntegrity.TryLoadManifest(snapshot.TranslationPath, out var translationManifest, out _)
            && translationManifest is not null
            && !ArtifactIntegrity.SegmentTimingMatches(
                translationManifest.SegmentTiming,
                transcriptTiming))
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

        if (string.IsNullOrWhiteSpace(snapshot.TtsPath))
        {
            error = "TTS artifact path was missing.";
            return false;
        }

        if (!File.Exists(snapshot.TtsPath))
        {
            error = $"Artifact file was missing: {snapshot.TtsPath}";
            return false;
        }

        if (!ArtifactIntegrity.HasManifest(snapshot.TtsPath))
        {
            if (!string.IsNullOrWhiteSpace(snapshot.MixedDubAudioPath) && !File.Exists(snapshot.MixedDubAudioPath))
            {
                error = $"Artifact file was missing: {snapshot.MixedDubAudioPath}";
                return false;
            }

            if (snapshot.TtsSegmentAudioPaths is { Count: > 0 })
            {
                foreach (var pair in snapshot.TtsSegmentAudioPaths)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)
                        || string.IsNullOrWhiteSpace(pair.Value)
                        || !File.Exists(pair.Value)
                        || new FileInfo(pair.Value).Length <= 0)
                    {
                        error = $"Validated TTS clip was missing for segment '{pair.Key}'.";
                        return false;
                    }
                }
            }

            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(snapshot.TtsSegmentsPath))
        {
            error = "TTS segment directory path was missing.";
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

        var translationTiming = ArtifactIntegrity.TryLoadManifest(snapshot.TranslationPath!, out var translationManifest, out _)
            && translationManifest is not null
                ? translationManifest.SegmentTiming
                : ArtifactIntegrity.BuildTranslationTimingSummary(translation.Segments);
        var translationHash = ArtifactIntegrity.TryGetArtifactSha256(snapshot.TranslationPath!)
            ?? throw new InvalidOperationException(
                $"Translation artifact hash could not be resolved for TTS validation: {snapshot.TranslationPath}");

        if (!ValidateDirectoryArtifact(
                snapshot.TtsSegmentsPath,
                "tts_segment_set",
                expectedSegmentIds,
                translationTiming,
                ArtifactIntegrity.BuildUpstreamHashes(("translation", snapshot.TranslationPath)),
                ArtifactIntegrity.ComputeTtsSegmentSetProvenanceDigest(
                    translationHash,
                    snapshot,
                    BuildTtsSettings(snapshot)),
                snapshot.TtsSegmentAudioPaths,
                out error))
        {
            return false;
        }

        var segmentManifest = ArtifactIntegrity.LoadManifest(snapshot.TtsSegmentsPath);
        var dubProvenance = ArtifactIntegrity.ComputeDubProvenanceDigest(
            segmentManifest.Sha256,
            ArtifactIntegrity.TryGetArtifactSha256(snapshot.AmbianceAudioPath),
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
                expectedTiming: translationTiming,
                expectedUpstreamHashes: upstream,
                expectedProvenanceDigest: dubProvenance,
                out error))
        {
            return false;
        }

        if (ArtifactIntegrity.TryLoadManifest(snapshot.TtsPath, out var dubManifest, out _)
            && dubManifest is not null
            && !ArtifactIntegrity.DurationsMatch(
                dubManifest.ProbedDurationSeconds,
                translationTiming?.DurationSeconds))
        {
            error = "Dub duration did not match the expected translation range.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.MixedDubAudioPath)
            && !ValidateFileArtifact(
                snapshot.MixedDubAudioPath,
                "dub_mixed",
                expectedSchemaVersion: null,
                expectedSegmentCount: expectedSegmentIds.Count,
                expectedSegmentIds: expectedSegmentIds,
                expectedTiming: translationTiming,
                expectedUpstreamHashes: upstream,
                expectedProvenanceDigest: dubProvenance,
                out error))
        {
            return false;
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

    private static bool ShouldValidateTranscriptChain(WorkflowSessionSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.TranscriptPath)
        || !string.IsNullOrWhiteSpace(snapshot.IngestedMediaPath)
        || !string.IsNullOrWhiteSpace(snapshot.VocalsAudioPath)
        || !string.IsNullOrWhiteSpace(snapshot.AmbianceAudioPath);
}
