using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

        if (!TryLoadManifestWhenPresent(snapshot.IngestedMediaPath, out var hasMediaManifest, out error))
            return false;

        if (!hasMediaManifest)
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

        if (!File.Exists(snapshot.VocalsAudioPath!) || !File.Exists(snapshot.AmbianceAudioPath!))
        {
            error = "Vocal separation audio file was missing.";
            return false;
        }

        if (!TryLoadManifestWhenPresent(snapshot.VocalsAudioPath!, out var vocalsHasManifest, out error))
            return false;
        if (!TryLoadManifestWhenPresent(snapshot.AmbianceAudioPath!, out var ambianceHasManifest, out error))
            return false;
        if (!TryLoadManifestWhenPresent(snapshot.IngestedMediaPath!, out var mediaHasManifest, out error))
            return false;

        if (!vocalsHasManifest || !ambianceHasManifest || !mediaHasManifest)
        {
            error = null;
            return true;
        }

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

        if (ArtifactIntegrity.TryLoadManifest(snapshot.IngestedMediaPath!, out var mediaManifest, out _)
            && mediaManifest is not null
            && ArtifactIntegrity.TryLoadManifest(snapshot.VocalsAudioPath!, out var vocalsManifest, out _)
            && vocalsManifest is not null
            && ArtifactIntegrity.TryLoadManifest(snapshot.AmbianceAudioPath!, out var ambianceManifest, out _)
            && ambianceManifest is not null)
        {
            if (!ArtifactIntegrity.DurationsMatch(vocalsManifest.ProbedDurationSeconds, mediaManifest.ProbedDurationSeconds))
            {
                error = "Vocals stem duration did not match the ingested media duration.";
                return false;
            }

            if (!ArtifactIntegrity.DurationsMatch(ambianceManifest.ProbedDurationSeconds, mediaManifest.ProbedDurationSeconds))
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
            error = $"Transcript file was missing: {snapshot.TranscriptPath}";
            return false;
        }

        var transcriptManifestPath = ArtifactPersistence.GetManifestPath(snapshot.TranscriptPath);
        if (!File.Exists(transcriptManifestPath))
        {
            error = null;
            return true;
        }

        if (!TryDeserializeTranscriptArtifact(snapshot.TranscriptPath, out var transcript, out error)
            || transcript is null)
        {
            return false;
        }

        var expectedTiming = ArtifactIntegrity.BuildTranscriptTimingSummary(transcript.Segments);
        var expectedSegmentIds = ArtifactIntegrity.BuildTranscriptSegmentIds(transcript.Segments);
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(
            ("media_copy", snapshot.IngestedMediaPath),
            ("vocals_stem", snapshot.VocalsAudioPath));
        double? mediaDuration = null;
        if (ArtifactIntegrity.TryLoadManifest(snapshot.IngestedMediaPath!, out var mediaManifestForDuration, out _)
            && mediaManifestForDuration is not null)
            mediaDuration = mediaManifestForDuration.ProbedDurationSeconds;
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

        if (mediaDuration.HasValue
            && ArtifactIntegrity.TryLoadManifest(snapshot.TranscriptPath, out var transcriptManifest, out _)
            && transcriptManifest is not null
            && !ArtifactIntegrity.DurationsMatch(
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

        if (!File.Exists(snapshot.TranslationPath))
        {
            error = $"Translation file was missing: {snapshot.TranslationPath}";
            return false;
        }

        var translationManifestPath = ArtifactPersistence.GetManifestPath(snapshot.TranslationPath);
        if (!File.Exists(translationManifestPath))
        {
            error = null;
            return true;
        }

        if (!TryDeserializeTranslationArtifact(snapshot.TranslationPath, out var translation, out error)
            || translation is null)
        {
            return false;
        }

        if (!TryDeserializeTranscriptArtifact(snapshot.TranscriptPath!, out var transcript, out error)
            || transcript is null)
        {
            return false;
        }

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

        ArtifactIntegrity.TryLoadManifest(snapshot.TranscriptPath!, out var transcriptManifest, out _);
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

        if (transcriptManifest is not null
            && ArtifactIntegrity.TryLoadManifest(snapshot.TranslationPath, out var translationManifest, out _)
            && translationManifest is not null
            && !ArtifactIntegrity.SegmentTimingMatches(
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
        if (string.IsNullOrWhiteSpace(snapshot.TtsPath) || !File.Exists(snapshot.TtsPath))
        {
            error = "TTS timeline output was missing.";
            return false;
        }

        var ttsManifestPath = ArtifactPersistence.GetManifestPath(snapshot.TtsPath);
        var segmentsManifestPath = string.IsNullOrWhiteSpace(snapshot.TtsSegmentsPath)
            ? null
            : ArtifactPersistence.GetManifestPath(snapshot.TtsSegmentsPath);
        var hasTtsManifest = File.Exists(ttsManifestPath);
        var hasSegmentsManifest = segmentsManifestPath is not null && File.Exists(segmentsManifestPath);

        if (!hasTtsManifest && !hasSegmentsManifest)
        {
            if (snapshot.TtsSegmentAudioPaths is { Count: > 0 })
            {
                error = "TTS segment clips require integrity manifests.";
                return false;
            }

            error = null;
            return true;
        }

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

        if (!TryDeserializeTranslationArtifact(snapshot.TranslationPath!, out var translation, out error)
            || translation is null)
        {
            return false;
        }

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

        if (!ArtifactIntegrity.TryLoadManifest(snapshot.TranslationPath!, out var translationManifest, out _)
            || translationManifest is null)
        {
            error = "Translation manifest was missing or unreadable.";
            return false;
        }

        if (!ValidateDirectoryArtifact(
                snapshot.TtsSegmentsPath,
                "tts_segment_set",
                expectedSegmentIds,
                ArtifactIntegrity.BuildTranslationTimingSummary(translation.Segments),
                ArtifactIntegrity.BuildUpstreamHashes(("translation", snapshot.TranslationPath)),
                ArtifactIntegrity.ComputeTtsSegmentSetProvenanceDigest(
                    translationManifest.Sha256,
                    snapshot,
                    BuildTtsSettings(snapshot)),
                snapshot.TtsSegmentAudioPaths,
                out error))
        {
            return false;
        }

        if (!ArtifactIntegrity.TryLoadManifest(snapshot.TtsSegmentsPath, out var segmentManifest, out _)
            || segmentManifest is null)
        {
            error = "TTS segments manifest was missing or unreadable.";
            return false;
        }

        string? ambianceSha = null;
        if (!string.IsNullOrWhiteSpace(snapshot.AmbianceAudioPath))
        {
            if (!ArtifactIntegrity.TryLoadManifest(snapshot.AmbianceAudioPath!, out var ambianceManifest, out _)
                || ambianceManifest is null)
            {
                error = "Ambiance stem manifest was missing or unreadable.";
                return false;
            }
            ambianceSha = ambianceManifest.Sha256;
        }

        var dubProvenance = ArtifactIntegrity.ComputeDubProvenanceDigest(
            segmentManifest.Sha256,
            ambianceSha,
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

        if (!ArtifactIntegrity.TryLoadManifest(snapshot.TtsPath, out var dubManifest, out _)
            || dubManifest is null)
        {
            error = "Dub timeline manifest was missing or unreadable.";
            return false;
        }

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

    private static bool TryDeserializeTranscriptArtifact(
        string path,
        out TranscriptArtifact? transcript,
        out string? error)
    {
        try
        {
            transcript = ArtifactJson.DeserializeTranscript(File.ReadAllText(path), path);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            transcript = null;
            error = $"Transcript artifact was unreadable: {ex.Message}";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            transcript = null;
            error = $"Transcript artifact was invalid: {ex.Message}";
            return false;
        }
        catch (IOException ex)
        {
            transcript = null;
            error = $"Transcript artifact was unreadable: {ex.Message}";
            return false;
        }
    }

    private static bool TryDeserializeTranslationArtifact(
        string path,
        out TranslationArtifact? translation,
        out string? error)
    {
        try
        {
            translation = ArtifactJson.DeserializeTranslation(File.ReadAllText(path), path);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            translation = null;
            error = $"Translation artifact was unreadable: {ex.Message}";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            translation = null;
            error = $"Translation artifact was invalid: {ex.Message}";
            return false;
        }
        catch (IOException ex)
        {
            translation = null;
            error = $"Translation artifact was unreadable: {ex.Message}";
            return false;
        }
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

    private static bool TryLoadManifestWhenPresent(string artifactPath, out bool hasManifest, out string? error)
    {
        var manifestPath = ArtifactPersistence.GetManifestPath(artifactPath);
        if (!File.Exists(manifestPath))
        {
            hasManifest = false;
            error = null;
            return true;
        }

        hasManifest = true;
        if (ArtifactIntegrity.TryLoadManifest(artifactPath, out _, out var loadError))
        {
            error = null;
            return true;
        }

        error = $"Artifact manifest was unreadable: {manifestPath}. {loadError}";
        return false;
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
