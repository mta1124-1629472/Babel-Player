using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Settings;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for <see cref="SessionSnapshotSemantics"/> — artifact validation,
/// pipeline invalidation, stage resolution, and clear-output helpers.
/// </summary>
public sealed class SessionSnapshotSemanticsTests : IDisposable
{
    private readonly string _dir;

    public SessionSnapshotSemanticsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-semantics-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    private string WriteFile(string name, string content = "placeholder")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private WorkflowSessionSnapshot WithValidatedMedia(WorkflowSessionSnapshot snapshot, string name = "video.mp4")
    {
        var path = WriteFile($"{Guid.NewGuid():N}-{name}", "media");
        ArtifactIntegrity.WriteFileManifestAsync(
                path,
                "media_copy",
                artifactSchemaVersion: null,
                probedDurationSeconds: null,
                segmentCount: null,
                segmentIds: null,
                segmentTiming: null,
                upstreamArtifactHashes: null,
                provenanceDigest: ArtifactIntegrity.ComputeCompositeSha256(["stage=media_copy"]))
            .GetAwaiter()
            .GetResult();
        return snapshot with { IngestedMediaPath = path };
    }

    private WorkflowSessionSnapshot WithValidatedStemPair(WorkflowSessionSnapshot snapshot)
    {
        var upstream = ArtifactIntegrity.BuildUpstreamHashes(("media_copy", snapshot.IngestedMediaPath));
        upstream.TryGetValue("media_copy", out var mediaHash);
        var provenance = ArtifactIntegrity.ComputeCompositeSha256(
        [
            "stage=vocal_separation",
            $"media_copy={mediaHash ?? string.Empty}",
        ]);

        var vocalsPath = WriteFile($"{Guid.NewGuid():N}-vocals.wav", "vocals");
        ArtifactIntegrity.WriteFileManifestAsync(
                vocalsPath,
                "vocals_stem",
                artifactSchemaVersion: null,
                probedDurationSeconds: null,
                segmentCount: null,
                segmentIds: null,
                segmentTiming: null,
                upstreamArtifactHashes: upstream,
                provenanceDigest: provenance)
            .GetAwaiter()
            .GetResult();

        var ambiancePath = WriteFile($"{Guid.NewGuid():N}-ambiance.wav", "ambiance");
        ArtifactIntegrity.WriteFileManifestAsync(
                ambiancePath,
                "ambiance_stem",
                artifactSchemaVersion: null,
                probedDurationSeconds: null,
                segmentCount: null,
                segmentIds: null,
                segmentTiming: null,
                upstreamArtifactHashes: upstream,
                provenanceDigest: provenance)
            .GetAwaiter()
            .GetResult();

        return snapshot with
        {
            VocalsAudioPath = vocalsPath,
            AmbianceAudioPath = ambiancePath,
        };
    }

    private WorkflowSessionSnapshot WithValidatedTranscript(WorkflowSessionSnapshot snapshot, string name = "transcript.json")
    {
        var artifact = new TranscriptArtifact
        {
            SchemaVersion = ArtifactJson.CurrentSchemaVersion,
            Language = snapshot.SourceLanguage ?? "es",
            LanguageProbability = 0.99,
            Segments =
            [
                new TranscriptSegmentArtifact
                {
                    Start = 0.0,
                    End = 1.0,
                    Text = "hola",
                },
            ],
        };

        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, ArtifactJson.SerializeTranscript(artifact));

        var upstream = ArtifactIntegrity.BuildUpstreamHashes(
            ("media_copy", snapshot.IngestedMediaPath),
            ("vocals_stem", snapshot.VocalsAudioPath));
        upstream.TryGetValue("media_copy", out var mediaHash);
        ArtifactIntegrity.WriteFileManifestAsync(
                path,
                "transcript",
                artifact.SchemaVersion,
                probedDurationSeconds: null,
                segmentCount: artifact.Segments!.Count,
                segmentIds: ArtifactIntegrity.BuildTranscriptSegmentIds(artifact.Segments),
                segmentTiming: ArtifactIntegrity.BuildTranscriptTimingSummary(artifact.Segments),
                upstreamArtifactHashes: upstream,
                provenanceDigest: ArtifactIntegrity.ComputeTranscriptionProvenanceDigest(
                    mediaHash,
                    !string.IsNullOrWhiteSpace(snapshot.VocalsAudioPath),
                    BuildTranscriptionSettings(snapshot)))
            .GetAwaiter()
            .GetResult();

        return snapshot with
        {
            TranscriptPath = path,
            SourceLanguage = artifact.Language,
        };
    }

    private WorkflowSessionSnapshot WithValidatedTranslation(WorkflowSessionSnapshot snapshot, string name = "translation.json")
    {
        var artifact = new TranslationArtifact
        {
            SchemaVersion = ArtifactJson.CurrentSchemaVersion,
            SourceLanguage = snapshot.SourceLanguage ?? "es",
            TargetLanguage = snapshot.TargetLanguage ?? "en",
            Segments =
            [
                new TranslationSegmentArtifact
                {
                    Id = "segment_0.0",
                    Start = 0.0,
                    End = 1.0,
                    Text = "hola",
                    TranslatedText = "hello",
                },
            ],
        };

        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, ArtifactJson.SerializeTranslation(artifact));

        var upstream = ArtifactIntegrity.BuildUpstreamHashes(("transcript", snapshot.TranscriptPath));
        upstream.TryGetValue("transcript", out var transcriptHash);
        ArtifactIntegrity.WriteFileManifestAsync(
                path,
                "translation",
                artifact.SchemaVersion,
                probedDurationSeconds: null,
                segmentCount: artifact.Segments!.Count,
                segmentIds: ArtifactIntegrity.BuildTranslationSegmentIds(artifact.Segments),
                segmentTiming: ArtifactIntegrity.BuildTranslationTimingSummary(artifact.Segments),
                upstreamArtifactHashes: upstream,
                provenanceDigest: ArtifactIntegrity.ComputeTranslationProvenanceDigest(
                    transcriptHash,
                    BuildTranslationSettings(snapshot),
                    artifact.SourceLanguage!,
                    artifact.TargetLanguage!))
            .GetAwaiter()
            .GetResult();

        return snapshot with
        {
            TranslationPath = path,
            TargetLanguage = artifact.TargetLanguage,
        };
    }

    private WorkflowSessionSnapshot WithValidatedTts(WorkflowSessionSnapshot snapshot, string ttsName = "tts.mp3")
    {
        var translation = ArtifactJson.LoadTranslationAsync(snapshot.TranslationPath!).GetAwaiter().GetResult();
        var segmentsDir = Path.Combine(_dir, $"{Guid.NewGuid():N}-segments");
        Directory.CreateDirectory(segmentsDir);

        var segmentAudioPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in translation.Segments ?? [])
        {
            var segmentPath = Path.Combine(segmentsDir, $"{segment.Id}.mp3");
            File.WriteAllText(segmentPath, $"audio:{segment.Id}");
            segmentAudioPaths[segment.Id!] = segmentPath;
        }

        var orderedPairs = (translation.Segments ?? [])
            .Select(segment => new KeyValuePair<string, string>(segment.Id!, segmentAudioPaths[segment.Id!]))
            .ToList();
        var segmentTiming = ArtifactIntegrity.BuildTranslationTimingSummary(translation.Segments);
        var segmentProvenance = ArtifactIntegrity.ComputeTtsSegmentSetProvenanceDigest(
            ArtifactIntegrity.LoadManifest(snapshot.TranslationPath!).Sha256,
            snapshot,
            BuildTtsSettings(snapshot));
        ArtifactIntegrity.WriteDirectoryManifestAsync(
                segmentsDir,
                "tts_segment_set",
                orderedPairs,
                probedDurationSeconds: null,
                segmentTiming: segmentTiming,
                upstreamArtifactHashes: ArtifactIntegrity.BuildUpstreamHashes(("translation", snapshot.TranslationPath)),
                provenanceDigest: segmentProvenance)
            .GetAwaiter()
            .GetResult();

        var ttsPath = WriteFile($"{Guid.NewGuid():N}-{ttsName}", "dub");
        var segmentManifest = ArtifactIntegrity.LoadManifest(segmentsDir);
        var dubUpstream = ArtifactIntegrity.BuildUpstreamHashes(
            ("tts_segment_set", segmentsDir),
            ("ambiance_stem", snapshot.AmbianceAudioPath));
        ArtifactIntegrity.WriteFileManifestAsync(
                ttsPath,
                "dub_timeline",
                artifactSchemaVersion: null,
                probedDurationSeconds: null,
                segmentCount: orderedPairs.Count,
                segmentIds: orderedPairs.Select(pair => pair.Key).ToList(),
                segmentTiming: segmentTiming,
                upstreamArtifactHashes: dubUpstream,
                provenanceDigest: ArtifactIntegrity.ComputeDubProvenanceDigest(
                    segmentManifest.Sha256,
                    !string.IsNullOrWhiteSpace(snapshot.AmbianceAudioPath)
                        ? ArtifactIntegrity.LoadManifest(snapshot.AmbianceAudioPath!).Sha256
                        : null,
                    BuildTtsSettings(snapshot)))
            .GetAwaiter()
            .GetResult();

        return snapshot with
        {
            TtsSegmentsPath = segmentsDir,
            TtsSegmentAudioPaths = segmentAudioPaths,
            TtsPath = ttsPath,
        };
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

    // ── ResolveArtifactStage ──────────────────────────────────────────────────

    [Fact]
    public void ResolveArtifactStage_Foundation_ReturnsFoundation()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        Assert.Equal(SessionWorkflowStage.Foundation, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public void ResolveArtifactStage_MediaLoaded_WithExistingFile_ReturnsMediaLoaded()
    {
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.MediaLoaded,
        });
        Assert.Equal(SessionWorkflowStage.MediaLoaded, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public void ResolveArtifactStage_MediaLoaded_MissingFile_ReturnsFoundation()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.MediaLoaded,
            IngestedMediaPath = Path.Combine(_dir, "nonexistent.mp4"),
        };
        Assert.Equal(SessionWorkflowStage.Foundation, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public void ResolveArtifactStage_Transcribed_WithBothFiles_ReturnsTranscribed()
    {
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
        });
        snap = WithValidatedTranscript(snap);
        Assert.Equal(SessionWorkflowStage.Transcribed, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public void ResolveArtifactStage_Transcribed_MissingTranscript_ReturnsMediaLoaded()
    {
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            TranscriptPath = Path.Combine(_dir, "missing-transcript.json"),
        });
        Assert.Equal(SessionWorkflowStage.MediaLoaded, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public void ResolveArtifactStage_Diarized_WithMarker_ReturnsDiarized()
    {
        var now = DateTimeOffset.UtcNow;
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(now) with
        {
            Stage = SessionWorkflowStage.Diarized,
            DiarizationProvider = ProviderNames.NemoLocal,
            SpeakersDetectedAtUtc = now,
        });
        snap = WithValidatedTranscript(snap);

        Assert.Equal(SessionWorkflowStage.Diarized, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public void ResolveArtifactStage_TtsGenerated_WithAllFiles_ReturnsTtsGenerated()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TtsVoice = "voice-a",
        };
        snap = WithValidatedMedia(snap);
        snap = WithValidatedTranscript(snap);
        snap = WithValidatedTranslation(snap);
        snap = WithValidatedTts(snap);
        Assert.Equal(SessionWorkflowStage.TtsGenerated, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public void ResolveArtifactStage_TtsGenerated_MissingTtsFile_ReturnsTranslated()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TtsPath = Path.Combine(_dir, "missing-tts.mp3"),
        };
        snap = WithValidatedMedia(snap);
        snap = WithValidatedTranscript(snap);
        snap = WithValidatedTranslation(snap);
        Assert.Equal(SessionWorkflowStage.Translated, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    // ── ValidateArtifacts ─────────────────────────────────────────────────────

    [Fact]
    public void ValidateArtifacts_Foundation_NoClearedArtifacts()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);
        Assert.Empty(result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.Foundation, result.Snapshot.Stage);
    }

    [Fact]
    public void ValidateArtifacts_TtsGeneratedWithAllFiles_NoClearedArtifacts()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TtsVoice = "voice-a",
        };
        snap = WithValidatedMedia(snap);
        snap = WithValidatedTranscript(snap);
        snap = WithValidatedTranslation(snap);
        snap = WithValidatedTts(snap);

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);
        Assert.Empty(result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.TtsGenerated, result.Snapshot.Stage);
    }

    [Fact]
    public void ValidateArtifacts_MissingTts_DegradesToTranslated()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TtsPath = Path.Combine(_dir, "missing.mp3"),
            TtsVoice = "some-voice",
        };
        snap = WithValidatedMedia(snap);
        snap = WithValidatedTranscript(snap);
        snap = WithValidatedTranslation(snap);

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);
        Assert.Contains("tts", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.Translated, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.TtsVoice);
    }

    [Fact]
    public void ValidateArtifacts_MissingTranslation_DegradesToTranscribed()
    {
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Translated,
            TranslationPath = Path.Combine(_dir, "missing-translation.json"),
            TargetLanguage = "en",
        });
        snap = WithValidatedTranscript(snap);

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);
        Assert.Contains("translation", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.Transcribed, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.TargetLanguage);
    }

    [Fact]
    public void ValidateArtifacts_MissingTranslation_WithDiarizationMarker_DegradesToDiarized()
    {
        var now = DateTimeOffset.UtcNow;
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(now) with
        {
            Stage = SessionWorkflowStage.Translated,
            TranslationPath = Path.Combine(_dir, "missing-translation.json"),
            TargetLanguage = "en",
            DiarizationProvider = ProviderNames.NemoLocal,
            SpeakersDetectedAtUtc = now,
        });
        snap = WithValidatedTranscript(snap);

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);

        Assert.Contains("translation", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.Diarized, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.TargetLanguage);
    }

    [Fact]
    public void ValidateArtifacts_DiarizedMissingMarker_DegradesToTranscribed()
    {
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Diarized,
            SpeakerVoiceAssignments = new() { ["spk_00"] = "voice-1" },
        });
        snap = WithValidatedTranscript(snap);

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);

        Assert.Contains("diarization", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.Transcribed, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.SpeakerVoiceAssignments);
    }

    [Fact]
    public void ValidateArtifacts_MissingTranscript_DegradesToMediaLoaded()
    {
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            TranscriptPath = Path.Combine(_dir, "missing-transcript.json"),
            SourceLanguage = "es",
        });

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);
        Assert.Contains("transcription", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.MediaLoaded, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.SourceLanguage);
    }

    [Fact]
    public void ValidateArtifacts_MissingVocalsStem_ClearsVocalSeparationArtifacts()
    {
        var missingVocalsPath = Path.Combine(_dir, "missing-vocals.wav");
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.MediaLoaded,
            VocalsAudioPath = missingVocalsPath,
        });
        var validStemPair = WithValidatedStemPair(snap);
        snap = snap with { AmbianceAudioPath = validStemPair.AmbianceAudioPath };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);

        Assert.Contains("vocal_separation", result.ClearedArtifacts);
        Assert.Null(result.Snapshot.VocalsAudioPath);
        Assert.Null(result.Snapshot.AmbianceAudioPath);
    }

    [Fact]
    public void ValidateArtifacts_MissingInstrumentalStem_WithNoVocalsPath_ClearsVocalSeparationArtifacts()
    {
        var missingInstrumentalPath = Path.Combine(_dir, "missing-instrumental.wav");
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.MediaLoaded,
            VocalsAudioPath = null,
            AmbianceAudioPath = missingInstrumentalPath,
        });

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);

        Assert.Contains("vocal_separation", result.ClearedArtifacts);
        Assert.Null(result.Snapshot.VocalsAudioPath);
        Assert.Null(result.Snapshot.AmbianceAudioPath);
    }

    [Fact]
    public void ValidateArtifacts_MissingVocalsStem_AtTranscribedStage_DegradesToMediaLoaded()
    {
        var missingVocalsPath = Path.Combine(_dir, "missing-vocals.wav");
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            VocalsAudioPath = missingVocalsPath,
        });
        var validStemPair = WithValidatedStemPair(snap);
        snap = snap with { AmbianceAudioPath = validStemPair.AmbianceAudioPath };
        snap = WithValidatedTranscript(snap);

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);

        Assert.Contains("transcription", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.MediaLoaded, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.VocalsAudioPath);
        Assert.Null(result.Snapshot.TranscriptPath);
        Assert.Null(result.Snapshot.SourceLanguage);
    }

    [Fact]
    public void ValidateArtifacts_MissingMedia_DegradesToFoundation()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.MediaLoaded,
            IngestedMediaPath = Path.Combine(_dir, "missing-video.mp4"),
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);
        Assert.Contains("media", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.Foundation, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.IngestedMediaPath);
    }

    [Fact]
    public void ValidateArtifacts_RecordsOriginalStage()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TtsPath = Path.Combine(_dir, "missing.mp3"),
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);
        Assert.Equal(SessionWorkflowStage.TtsGenerated, result.OriginalStage);
    }

    // ── ComputeInvalidation ───────────────────────────────────────────────────

    [Fact]
    public void ComputeInvalidation_FoundationStage_ReturnsNone()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        var settings = new AppSettings();
        Assert.Equal(PipelineInvalidation.None, SessionSnapshotSemantics.ComputeInvalidation(snap, settings));
    }

    [Fact]
    public void ComputeInvalidation_TranscribedStageNoChange_ReturnsNone()
    {
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        });
        snap = WithValidatedTranscript(snap);
        var settings = new AppSettings
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        };
        Assert.Equal(PipelineInvalidation.None, SessionSnapshotSemantics.ComputeInvalidation(snap, settings));
    }

    [Fact]
    public void ComputeInvalidation_TranscribedStageModelChanged_ReturnsTranscription()
    {
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        });
        snap = WithValidatedTranscript(snap);
        var settings = new AppSettings
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "large-v3",
        };
        Assert.Equal(PipelineInvalidation.Transcription, SessionSnapshotSemantics.ComputeInvalidation(snap, settings));
    }

    [Fact]
    public void ComputeInvalidation_TranscribedStage_VocalSeparationToggleChanged_ReturnsTranscription()
    {
        var snap = WithValidatedMedia(WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        });
        snap = WithValidatedStemPair(snap);
        snap = WithValidatedTranscript(snap);
        var settings = new AppSettings
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            VocalSeparationEnabled = false,
        };

        Assert.Equal(PipelineInvalidation.Transcription, SessionSnapshotSemantics.ComputeInvalidation(snap, settings));
    }

    [Fact]
    public void ComputeInvalidation_TtsStageOnlyTtsChanged_ReturnsTts()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            TargetLanguage = "en",
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "en-US-JennyNeural",
        };
        snap = WithValidatedMedia(snap);
        snap = WithValidatedTranscript(snap);
        snap = WithValidatedTranslation(snap);
        snap = WithValidatedTts(snap);
        var settings = new AppSettings
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationProfile = ComputeProfile.Cloud,
            TranslationModel = "default",
            TargetLanguage = "en",
            TtsProvider = ProviderNames.EdgeTts,
            TtsProfile = ComputeProfile.Cloud,
            TtsVoice = "en-US-AriaNeural", // changed voice
        };
        Assert.Equal(PipelineInvalidation.Tts, SessionSnapshotSemantics.ComputeInvalidation(snap, settings));
    }

    [Fact]
    public void ComputeInvalidation_TtsStageTargetLanguageChanged_ReturnsTranslation()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            TargetLanguage = "en",
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "en-US-JennyNeural",
        };
        snap = WithValidatedMedia(snap);
        snap = WithValidatedTranscript(snap);
        snap = WithValidatedTranslation(snap);
        snap = WithValidatedTts(snap);
        var settings = new AppSettings
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            TargetLanguage = "fr", // different target language
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "en-US-JennyNeural",
        };
        Assert.Equal(PipelineInvalidation.Translation, SessionSnapshotSemantics.ComputeInvalidation(snap, settings));
    }

    // ── ClearTtsOutputs ───────────────────────────────────────────────────────

    [Fact]
    public void ClearTtsOutputs_ClearsTtsFields()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TtsPath = "/some/path/tts.mp3",
            TtsVoice = "en-US-Jenny",
            TtsProvider = ProviderNames.EdgeTts,
            TtsGeneratedAtUtc = DateTimeOffset.UtcNow,
        };

        var result = SessionSnapshotSemantics.ClearTtsOutputs(snap);
        Assert.Null(result.TtsPath);
        Assert.Null(result.TtsVoice);
        Assert.Null(result.TtsProvider);
        Assert.Null(result.TtsGeneratedAtUtc);
        Assert.Null(result.TtsSegmentsPath);
        Assert.Null(result.TtsSegmentAudioPaths);
        Assert.Null(result.TtsSegmentDurations);
        Assert.Null(result.TtsRuntime);
    }

    // ── ClearTranslationOutputs ───────────────────────────────────────────────

    [Fact]
    public void ClearTranslationOutputs_ClearsTranslationAndTtsFields()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranslationPath = "/some/path/translation.json",
            TargetLanguage = "en",
            TtsPath = "/some/path/tts.mp3",
            TtsVoice = "en-US-Jenny",
        };

        var result = SessionSnapshotSemantics.ClearTranslationOutputs(snap);
        Assert.Null(result.TranslationPath);
        Assert.Null(result.TargetLanguage);
        Assert.Null(result.TranslatedAtUtc);
        Assert.Null(result.TranslationProvider);
        Assert.Null(result.TranslationModel);
        // TTS should also be cleared
        Assert.Null(result.TtsPath);
        Assert.Null(result.TtsVoice);
        Assert.Null(result.TtsSegmentDurations);
    }

    // ── ClearTranscriptionOutputs ─────────────────────────────────────────────

    [Fact]
    public void ClearTranscriptionOutputs_ClearsAllDownstreamFields()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptPath = "/some/transcript.json",
            SourceLanguage = "es",
            VocalsAudioPath = "/some/vocals.wav",
            AmbianceAudioPath = "/some/instrumental.wav",
            TranslationPath = "/some/translation.json",
            TargetLanguage = "en",
            TtsPath = "/some/tts.mp3",
        };

        var result = SessionSnapshotSemantics.ClearTranscriptionOutputs(snap);
        Assert.Null(result.TranscriptPath);
        Assert.Null(result.SourceLanguage);
        Assert.Null(result.VocalsAudioPath);
        Assert.Null(result.AmbianceAudioPath);
        Assert.Null(result.TranscribedAtUtc);
        Assert.Null(result.TranscriptionProvider);
        Assert.Null(result.TranscriptionModel);
        Assert.Null(result.TranslationPath);
        Assert.Null(result.TtsPath);
    }

    // ── ClearMediaLoadedOutputs ───────────────────────────────────────────────

    [Fact]
    public void ClearMediaLoadedOutputs_ClearsAllFields()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            IngestedMediaPath = "/some/video.mp4",
            TranscriptPath = "/some/transcript.json",
            TranslationPath = "/some/translation.json",
            TtsPath = "/some/tts.mp3",
        };

        var result = SessionSnapshotSemantics.ClearMediaLoadedOutputs(snap);
        Assert.Null(result.IngestedMediaPath);
        Assert.Null(result.MediaLoadedAtUtc);
        Assert.Null(result.TranscriptPath);
        Assert.Null(result.TranslationPath);
        Assert.Null(result.TtsPath);
    }

    // ── DescribeSessionProvenance ─────────────────────────────────────────────

    [Fact]
    public void DescribeSessionProvenance_ContainsStageAndProviders()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Translated,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranslationProvider = ProviderNames.Deepl,
            TtsProvider = ProviderNames.EdgeTts,
            SourceLanguage = "es",
            TargetLanguage = "en",
        };

        var desc = SessionSnapshotSemantics.DescribeSessionProvenance(snap);
        Assert.Contains("Translated", desc);
        Assert.Contains(ProviderNames.FasterWhisper, desc);
        Assert.Contains(ProviderNames.Deepl, desc);
        Assert.Contains("es", desc);
        Assert.Contains("en", desc);
    }

    [Fact]
    public void DescribeSessionProvenance_NullFields_ShowsNullPlaceholder()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        var desc = SessionSnapshotSemantics.DescribeSessionProvenance(snap);
        Assert.Contains("<null>", desc);
    }
}
