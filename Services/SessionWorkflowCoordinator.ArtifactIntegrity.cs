using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    private void WriteMediaManifest(string mediaPath)
    {
        var duration = _audioProcessingService is null
            ? null
            : _audioProcessingService.ProbeDurationAsync(mediaPath, CancellationToken.None).GetAwaiter().GetResult();
        ArtifactIntegrity.WriteFileManifestAsync(
                mediaPath,
                "media_copy",
                artifactSchemaVersion: null,
                probedDurationSeconds: duration,
                segmentCount: null,
                segmentIds: null,
                segmentTiming: null,
                upstreamArtifactHashes: null,
                provenanceDigest: ArtifactIntegrity.ComputeCompositeSha256(["stage=media_copy"]),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private async Task WriteStemManifestsAsync(
        string vocalsPath,
        string ambiancePath,
        CancellationToken cancellationToken)
    {
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(("media_copy", CurrentSession.IngestedMediaPath));
        upstream.TryGetValue("media_copy", out var mediaHash);
        var provenance = ArtifactIntegrity.ComputeCompositeSha256(
        [
            "stage=vocal_separation",
            $"media_copy={mediaHash ?? string.Empty}",
        ]);
        var vocalsDuration = _audioProcessingService is null
            ? null
            : await _audioProcessingService.ProbeDurationAsync(vocalsPath, cancellationToken).ConfigureAwait(false);
        var ambianceDuration = _audioProcessingService is null
            ? null
            : await _audioProcessingService.ProbeDurationAsync(ambiancePath, cancellationToken).ConfigureAwait(false);

        await ArtifactIntegrity.WriteFileManifestAsync(
                vocalsPath,
                "vocals_stem",
                artifactSchemaVersion: null,
                probedDurationSeconds: vocalsDuration,
                segmentCount: null,
                segmentIds: null,
                segmentTiming: null,
                upstreamArtifactHashes: upstream,
                provenanceDigest: provenance,
                cancellationToken)
            .ConfigureAwait(false);
        await ArtifactIntegrity.WriteFileManifestAsync(
                ambiancePath,
                "ambiance_stem",
                artifactSchemaVersion: null,
                probedDurationSeconds: ambianceDuration,
                segmentCount: null,
                segmentIds: null,
                segmentTiming: null,
                upstreamArtifactHashes: upstream,
                provenanceDigest: provenance,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteTranscriptManifestAsync(
        string transcriptPath,
        TranscriptArtifact artifact,
        CancellationToken cancellationToken)
    {
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(
            ("media_copy", CurrentSession.IngestedMediaPath),
            ("vocals_stem", CurrentSession.VocalsAudioPath));
        upstream.TryGetValue("media_copy", out var mediaHash);
        var provenance = ArtifactIntegrity.ComputeTranscriptionProvenanceDigest(
            mediaHash,
            !string.IsNullOrWhiteSpace(CurrentSession.VocalsAudioPath),
            CurrentSettings);
        await ArtifactIntegrity.WriteFileManifestAsync(
                transcriptPath,
                "transcript",
                artifact.SchemaVersion,
                probedDurationSeconds: null,
                segmentCount: artifact.Segments?.Count ?? 0,
                segmentIds: ArtifactIntegrity.BuildTranscriptSegmentIds(artifact.Segments),
                segmentTiming: ArtifactIntegrity.BuildTranscriptTimingSummary(artifact.Segments),
                upstreamArtifactHashes: upstream,
                provenanceDigest: provenance,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteTranslationManifestAsync(
        string translationPath,
        TranslationArtifact artifact,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(("transcript", CurrentSession.TranscriptPath));
        upstream.TryGetValue("transcript", out var transcriptHash);
        var provenance = ArtifactIntegrity.ComputeTranslationProvenanceDigest(
            transcriptHash,
            CurrentSettings,
            sourceLanguage,
            targetLanguage);
        await ArtifactIntegrity.WriteFileManifestAsync(
                translationPath,
                "translation",
                artifact.SchemaVersion,
                probedDurationSeconds: null,
                segmentCount: artifact.Segments?.Count ?? 0,
                segmentIds: ArtifactIntegrity.BuildTranslationSegmentIds(artifact.Segments),
                segmentTiming: ArtifactIntegrity.BuildTranslationTimingSummary(artifact.Segments),
                upstreamArtifactHashes: upstream,
                provenanceDigest: provenance,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteTtsIntegrityArtifactsAsync(
        WorkflowSessionSnapshot candidateSnapshot,
        string ttsPath,
        DubRenderResult renderResult,
        string segmentsDir,
        IReadOnlyDictionary<string, string> segmentAudioPaths,
        IReadOnlyList<TranslationSegmentArtifact> orderedSegments,
        CancellationToken cancellationToken)
    {
        var orderedPairs = orderedSegments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Id))
            .Select(segment => new KeyValuePair<string, string>(
                segment.Id!,
                segmentAudioPaths[segment.Id!]))
            .ToList();
        var translationHash = ArtifactIntegrity.TryGetArtifactSha256(candidateSnapshot.TranslationPath)
            ?? throw new InvalidOperationException(
                $"Translation artifact hash could not be resolved for TTS integrity: {candidateSnapshot.TranslationPath ?? "<null>"}");
        var segmentUpstream = ArtifactIntegrity.BuildUpstreamHashes(("translation", candidateSnapshot.TranslationPath));
        var segmentTiming = ArtifactIntegrity.BuildTranslationTimingSummary(orderedSegments);
        var segmentProvenance = ArtifactIntegrity.ComputeTtsSegmentSetProvenanceDigest(
            translationHash,
            candidateSnapshot,
            CurrentSettings);
        var segmentManifest = await ArtifactIntegrity.WriteDirectoryManifestAsync(
                segmentsDir,
                "tts_segment_set",
                orderedPairs,
                probedDurationSeconds: null,
                segmentTiming: segmentTiming,
                upstreamArtifactHashes: segmentUpstream,
                provenanceDigest: segmentProvenance,
                cancellationToken)
            .ConfigureAwait(false);

        var ambianceHash = ArtifactIntegrity.TryGetArtifactSha256(candidateSnapshot.AmbianceAudioPath);
        var dubUpstream = ArtifactIntegrity.BuildUpstreamHashes(
            ("tts_segment_set", segmentsDir),
            ("ambiance_stem", candidateSnapshot.AmbianceAudioPath));
        var dubProvenance = ArtifactIntegrity.ComputeDubProvenanceDigest(
            segmentManifest.Sha256,
            ambianceHash,
            CurrentSettings);
        var dubDuration = _audioProcessingService is null
            ? null
            : await _audioProcessingService.ProbeDurationAsync(ttsPath, cancellationToken).ConfigureAwait(false);
        await ArtifactIntegrity.WriteFileManifestAsync(
                ttsPath,
                "dub_timeline",
                artifactSchemaVersion: null,
                probedDurationSeconds: dubDuration,
                segmentCount: orderedPairs.Count,
                segmentIds: [.. orderedPairs.Select(pair => pair.Key)],
                segmentTiming: segmentTiming,
                upstreamArtifactHashes: dubUpstream,
                provenanceDigest: dubProvenance,
                cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(renderResult.MixedWithAmbiancePath) && File.Exists(renderResult.MixedWithAmbiancePath))
        {
            var mixedDuration = _audioProcessingService is null
                ? null
                : await _audioProcessingService.ProbeDurationAsync(renderResult.MixedWithAmbiancePath, cancellationToken).ConfigureAwait(false);
            await ArtifactIntegrity.WriteFileManifestAsync(
                    renderResult.MixedWithAmbiancePath,
                    "dub_mixed",
                    artifactSchemaVersion: null,
                    probedDurationSeconds: mixedDuration,
                    segmentCount: orderedPairs.Count,
                    segmentIds: [.. orderedPairs.Select(pair => pair.Key)],
                    segmentTiming: segmentTiming,
                    upstreamArtifactHashes: dubUpstream,
                    provenanceDigest: dubProvenance,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
