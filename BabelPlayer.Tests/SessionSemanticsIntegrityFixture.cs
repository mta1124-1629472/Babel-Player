using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

internal static class SessionSemanticsIntegrityFixture
{
    private static AppSettings BuildTranscriptionSettings(WorkflowSessionSnapshot snapshot) =>
        new()
        {
            TranscriptionRuntime = snapshot.TranscriptionRuntime
                ?? InferenceRuntimeCatalog.InferTranscriptionRuntime(snapshot.TranscriptionProvider),
            TranscriptionProvider = snapshot.TranscriptionProvider ?? string.Empty,
            TranscriptionModel = snapshot.TranscriptionModel ?? string.Empty,
            TranscriptionLanguageHint = snapshot.TranscriptionLanguageHint,
        };

    private static AppSettings BuildTranslationSettings(WorkflowSessionSnapshot snapshot) =>
        new()
        {
            TranslationRuntime = snapshot.TranslationRuntime
                ?? InferenceRuntimeCatalog.InferTranslationRuntime(snapshot.TranslationProvider),
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

    public static async Task<string> WriteMediaCopyAsync(string directory, CancellationToken ct = default)
    {
        var path = Path.Combine(directory, $"video-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, [0x47, 0x40], ct);
        await ArtifactIntegrity.WriteFileManifestAsync(
            path,
            "media_copy",
            artifactSchemaVersion: null,
            probedDurationSeconds: null,
            segmentCount: null,
            segmentIds: null,
            segmentTiming: null,
            upstreamArtifactHashes: null,
            provenanceDigest: ArtifactIntegrity.ComputeCompositeSha256(["stage=media_copy"]),
            ct);
        return path;
    }

    public static async Task<(string VocalsPath, string AmbiancePath)> WriteStemPairAsync(
        string directory,
        string mediaPath,
        CancellationToken ct = default)
    {
        var vocals = Path.Combine(directory, $"vocals-{Guid.NewGuid():N}.wav");
        var ambiance = Path.Combine(directory, $"ambiance-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(vocals, [0x01], ct);
        await File.WriteAllBytesAsync(ambiance, [0x02], ct);
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(("media_copy", mediaPath));
        upstream.TryGetValue("media_copy", out var mediaHash);
        var provenance = ArtifactIntegrity.ComputeCompositeSha256(
        [
            "stage=vocal_separation",
            $"media_copy={mediaHash ?? string.Empty}",
        ]);
        const double duration = 1.0;
        await ArtifactIntegrity.WriteFileManifestAsync(
                vocals,
                "vocals_stem",
                artifactSchemaVersion: null,
                probedDurationSeconds: duration,
                segmentCount: null,
                segmentIds: null,
                segmentTiming: null,
                upstreamArtifactHashes: upstream,
                provenanceDigest: provenance,
                ct)
            .ConfigureAwait(false);
        await ArtifactIntegrity.WriteFileManifestAsync(
                ambiance,
                "ambiance_stem",
                artifactSchemaVersion: null,
                probedDurationSeconds: duration,
                segmentCount: null,
                segmentIds: null,
                segmentTiming: null,
                upstreamArtifactHashes: upstream,
                provenanceDigest: provenance,
                ct)
            .ConfigureAwait(false);
        return (vocals, ambiance);
    }

    public static async Task<string> WriteTranscriptAsync(
        string directory,
        string mediaPath,
        WorkflowSessionSnapshot snapshot,
        CancellationToken ct = default)
    {
        var path = Path.Combine(directory, $"transcript-{Guid.NewGuid():N}.json");
        var artifact = new TranscriptArtifact
        {
            SchemaVersion = ArtifactJson.CurrentSchemaVersion,
            Language = "en",
            Segments =
            [
                new TranscriptSegmentArtifact { Start = 0.0, End = 1.0, Text = "hello" },
            ],
        };
        await File.WriteAllTextAsync(path, ArtifactJson.SerializeTranscript(artifact), ct);
        var parsed = ArtifactJson.DeserializeTranscript(await File.ReadAllTextAsync(path, ct), path);
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(
            ("media_copy", mediaPath),
            ("vocals_stem", snapshot.VocalsAudioPath));
        upstream.TryGetValue("media_copy", out var mediaHash);
        var prov = ArtifactIntegrity.ComputeTranscriptionProvenanceDigest(
            mediaHash,
            !string.IsNullOrWhiteSpace(snapshot.VocalsAudioPath),
            BuildTranscriptionSettings(snapshot));
        await ArtifactIntegrity.WriteFileManifestAsync(
                path,
                "transcript",
                parsed.SchemaVersion,
                probedDurationSeconds: null,
                segmentCount: parsed.Segments?.Count ?? 0,
                ArtifactIntegrity.BuildTranscriptSegmentIds(parsed.Segments),
                ArtifactIntegrity.BuildTranscriptTimingSummary(parsed.Segments),
                upstream,
                prov,
                ct)
            .ConfigureAwait(false);
        return path;
    }

    public static async Task<string> WriteTranslationAsync(
        string directory,
        string transcriptPath,
        WorkflowSessionSnapshot snapshot,
        CancellationToken ct = default)
    {
        var path = Path.Combine(directory, $"translation-{Guid.NewGuid():N}.json");
        var segmentId = SessionWorkflowCoordinator.SegmentId(0.0);
        var artifact = new TranslationArtifact
        {
            SchemaVersion = ArtifactJson.CurrentSchemaVersion,
            SourceLanguage = snapshot.SourceLanguage ?? "es",
            TargetLanguage = snapshot.TargetLanguage ?? "en",
            Segments =
            [
                new TranslationSegmentArtifact
                {
                    Id = segmentId,
                    Start = 0.0,
                    End = 1.0,
                    Text = "hola",
                    TranslatedText = "hello",
                },
            ],
        };
        await File.WriteAllTextAsync(path, ArtifactJson.SerializeTranslation(artifact), ct);
        var parsed = ArtifactJson.DeserializeTranslation(await File.ReadAllTextAsync(path, ct), path);
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(("transcript", transcriptPath));
        upstream.TryGetValue("transcript", out var transcriptHash);
        var prov = ArtifactIntegrity.ComputeTranslationProvenanceDigest(
            transcriptHash,
            BuildTranslationSettings(snapshot),
            parsed.SourceLanguage ?? string.Empty,
            parsed.TargetLanguage ?? string.Empty);
        await ArtifactIntegrity.WriteFileManifestAsync(
                path,
                "translation",
                parsed.SchemaVersion,
                probedDurationSeconds: null,
                segmentCount: parsed.Segments?.Count ?? 0,
                ArtifactIntegrity.BuildTranslationSegmentIds(parsed.Segments),
                ArtifactIntegrity.BuildTranslationTimingSummary(parsed.Segments),
                upstream,
                prov,
                ct)
            .ConfigureAwait(false);
        return path;
    }

    public static async Task<(string TtsPath, string SegmentsDir, Dictionary<string, string> SegmentPaths)> WriteTtsBundleAsync(
        string directory,
        string translationPath,
        WorkflowSessionSnapshot snapshot,
        CancellationToken ct = default)
    {
        var segmentsDir = Path.Combine(directory, $"tts-seg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(segmentsDir);
        var segmentId = SessionWorkflowCoordinator.SegmentId(0.0);
        var segPath = Path.Combine(segmentsDir, $"{segmentId}.mp3");
        await File.WriteAllBytesAsync(segPath, [0x03, 0x04], ct);
        var paths = new Dictionary<string, string> { [segmentId] = segPath };
        var ordered = new List<KeyValuePair<string, string>> { new(segmentId, segPath) };
        var translationJson = await File.ReadAllTextAsync(translationPath, ct);
        var translationArtifact = ArtifactJson.DeserializeTranslation(translationJson, translationPath);
        var translationManifestSha = ArtifactIntegrity.LoadManifest(translationPath).Sha256;
        var segmentUpstream = ArtifactIntegrity.BuildUpstreamHashes(("translation", translationPath));
        var segmentTiming = ArtifactIntegrity.BuildTranslationTimingSummary(translationArtifact.Segments);
        var segmentProvenance = ArtifactIntegrity.ComputeTtsSegmentSetProvenanceDigest(
            translationManifestSha,
            snapshot,
            BuildTtsSettings(snapshot));
        var segmentManifest = await ArtifactIntegrity.WriteDirectoryManifestAsync(
                segmentsDir,
                "tts_segment_set",
                ordered,
                probedDurationSeconds: null,
                segmentTiming: segmentTiming,
                upstreamArtifactHashes: segmentUpstream,
                provenanceDigest: segmentProvenance,
                ct)
            .ConfigureAwait(false);
        var ttsPath = Path.Combine(directory, $"tts-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(ttsPath, [0x05, 0x06, 0x07], ct);
        var dubUpstream = ArtifactIntegrity.BuildUpstreamHashes(
            ("tts_segment_set", segmentsDir),
            ("ambiance_stem", snapshot.AmbianceAudioPath));
        var dubProv = ArtifactIntegrity.ComputeDubProvenanceDigest(
            segmentManifest.Sha256,
            !string.IsNullOrWhiteSpace(snapshot.AmbianceAudioPath)
                ? ArtifactIntegrity.LoadManifest(snapshot.AmbianceAudioPath!).Sha256
                : null,
            BuildTtsSettings(snapshot));
        var transManifest = ArtifactIntegrity.LoadManifest(translationPath);
        await ArtifactIntegrity.WriteFileManifestAsync(
                ttsPath,
                "dub_timeline",
                artifactSchemaVersion: null,
                probedDurationSeconds: null,
                segmentCount: ordered.Count,
                segmentIds: [segmentId],
                segmentTiming: transManifest.SegmentTiming,
                upstreamArtifactHashes: dubUpstream,
                provenanceDigest: dubProv,
                ct)
            .ConfigureAwait(false);
        return (ttsPath, segmentsDir, paths);
    }

    public static async Task RewriteTranslationFileWithManifestAsync(
        string translationPath,
        TranslationArtifact artifact,
        string transcriptPath,
        WorkflowSessionSnapshot snapshot,
        CancellationToken ct = default)
    {
        await File.WriteAllTextAsync(translationPath, ArtifactJson.SerializeTranslation(artifact), ct);
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(("transcript", transcriptPath));
        upstream.TryGetValue("transcript", out var transcriptHash);
        var prov = ArtifactIntegrity.ComputeTranslationProvenanceDigest(
            transcriptHash,
            BuildTranslationSettings(snapshot),
            artifact.SourceLanguage ?? string.Empty,
            artifact.TargetLanguage ?? string.Empty);
        await ArtifactIntegrity.WriteFileManifestAsync(
                translationPath,
                "translation",
                artifact.SchemaVersion,
                probedDurationSeconds: null,
                segmentCount: artifact.Segments?.Count ?? 0,
                ArtifactIntegrity.BuildTranslationSegmentIds(artifact.Segments),
                ArtifactIntegrity.BuildTranslationTimingSummary(artifact.Segments),
                upstream,
                prov,
                ct)
            .ConfigureAwait(false);
    }
}
