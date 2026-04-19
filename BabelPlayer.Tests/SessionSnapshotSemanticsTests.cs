using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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

    // ── ResolveArtifactStage ──────────────────────────────────────────────────

    [Fact]
    public void ResolveArtifactStage_Foundation_ReturnsFoundation()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        Assert.Equal(SessionWorkflowStage.Foundation, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public async Task ResolveArtifactStage_MediaLoaded_WithExistingFile_ReturnsMediaLoaded()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.MediaLoaded,
            IngestedMediaPath = mediaPath,
        };
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
    public async Task ResolveArtifactStage_Transcribed_WithBothFiles_ReturnsTranscribed()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var snap = template with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
        };
        Assert.Equal(SessionWorkflowStage.Transcribed, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public async Task ResolveArtifactStage_Transcribed_MissingTranscript_ReturnsMediaLoaded()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = Path.Combine(_dir, "missing-transcript.json"),
        };
        Assert.Equal(SessionWorkflowStage.MediaLoaded, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public async Task ResolveArtifactStage_Diarized_WithMarker_ReturnsDiarized()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var now = DateTimeOffset.UtcNow;
        var template = WorkflowSessionSnapshot.CreateNew(now) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var snap = template with
        {
            Stage = SessionWorkflowStage.Diarized,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            DiarizationProvider = ProviderNames.NemoLocal,
            SpeakersDetectedAtUtc = now,
        };

        Assert.Equal(SessionWorkflowStage.Diarized, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public async Task ResolveArtifactStage_TtsGenerated_WithAllFiles_ReturnsTtsGenerated()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            SourceLanguage = "es",
            TargetLanguage = "en",
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "en-US-JennyNeural",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var translationPath = await SessionSemanticsIntegrityFixture.WriteTranslationAsync(_dir, transcriptPath, template);
        var snapForTts = template with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
        };
        var (ttsPath, segmentsDir, segPaths) =
            await SessionSemanticsIntegrityFixture.WriteTtsBundleAsync(_dir, translationPath, snapForTts);
        var snap = snapForTts with
        {
            TtsPath = ttsPath,
            TtsSegmentsPath = segmentsDir,
            TtsSegmentAudioPaths = segPaths,
        };
        Assert.Equal(SessionWorkflowStage.TtsGenerated, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public async Task ResolveArtifactStage_TtsGenerated_MissingTtsFile_ReturnsTranslated()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            SourceLanguage = "es",
            TargetLanguage = "en",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var translationPath = await SessionSemanticsIntegrityFixture.WriteTranslationAsync(_dir, transcriptPath, template);
        var snap = template with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
            TtsPath = Path.Combine(_dir, "missing-tts.mp3"),
        };
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
    public async Task ValidateArtifacts_TtsGeneratedWithAllFiles_NoClearedArtifacts()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            SourceLanguage = "es",
            TargetLanguage = "en",
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "en-US-JennyNeural",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var translationPath = await SessionSemanticsIntegrityFixture.WriteTranslationAsync(_dir, transcriptPath, template);
        var snapForTts = template with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
        };
        var (ttsPath, segmentsDir, segPaths) =
            await SessionSemanticsIntegrityFixture.WriteTtsBundleAsync(_dir, translationPath, snapForTts);
        var snap = snapForTts with
        {
            TtsPath = ttsPath,
            TtsSegmentsPath = segmentsDir,
            TtsSegmentAudioPaths = segPaths,
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);
        Assert.Empty(result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.TtsGenerated, result.Snapshot.Stage);
    }

    [Fact]
    public async Task ValidateArtifacts_MissingTts_DegradesToTranslated()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            SourceLanguage = "es",
            TargetLanguage = "en",
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "some-voice",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var translationPath = await SessionSemanticsIntegrityFixture.WriteTranslationAsync(_dir, transcriptPath, template);
        var snap = template with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
            TtsPath = Path.Combine(_dir, "missing.mp3"),
            TtsVoice = "some-voice",
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);
        Assert.Contains("tts", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.Translated, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.TtsVoice);
    }

    [Fact]
    public async Task ValidateArtifacts_MissingTranslation_DegradesToTranscribed()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            TargetLanguage = "en",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var snap = template with
        {
            Stage = SessionWorkflowStage.Translated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = Path.Combine(_dir, "missing-translation.json"),
            TargetLanguage = "en",
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);
        Assert.Contains("translation", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.Transcribed, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.TargetLanguage);
    }

    [Fact]
    public async Task ValidateArtifacts_MissingTranslation_WithDiarizationMarker_DegradesToDiarized()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var now = DateTimeOffset.UtcNow;
        var template = WorkflowSessionSnapshot.CreateNew(now) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            TargetLanguage = "en",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var snap = template with
        {
            Stage = SessionWorkflowStage.Translated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = Path.Combine(_dir, "missing-translation.json"),
            TargetLanguage = "en",
            DiarizationProvider = ProviderNames.NemoLocal,
            SpeakersDetectedAtUtc = now,
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);

        Assert.Contains("translation", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.Diarized, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.TargetLanguage);
    }

    [Fact]
    public async Task ValidateArtifacts_DiarizedMissingMarker_DegradesToTranscribed()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            SourceLanguage = "es",
            TargetLanguage = "en",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var translationPath = await SessionSemanticsIntegrityFixture.WriteTranslationAsync(_dir, transcriptPath, template);
        var snap = template with
        {
            Stage = SessionWorkflowStage.Diarized,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
            SpeakerVoiceAssignments = new Dictionary<string, string> { ["spk_00"] = "voice-1" },
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);

        Assert.Contains("diarization", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.Transcribed, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.SpeakerVoiceAssignments);
    }

    [Fact]
    public async Task ValidateArtifacts_MissingTranscript_DegradesToMediaLoaded()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = Path.Combine(_dir, "missing-transcript.json"),
            SourceLanguage = "es",
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);
        Assert.Contains("transcription", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.MediaLoaded, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.SourceLanguage);
    }

    [Fact]
    public async Task ValidateArtifacts_MissingTranscript_WithDiarizationMarker_RecordsDiarization()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var now = DateTimeOffset.UtcNow;
        var template = WorkflowSessionSnapshot.CreateNew(now) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        };
        await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var snap = template with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = Path.Combine(_dir, "missing-transcript.json"),
            SourceLanguage = "es",
            DiarizationProvider = ProviderNames.NemoLocal,
            SpeakersDetectedAtUtc = now,
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);

        Assert.Contains("diarization", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.MediaLoaded, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.DiarizationProvider);
        Assert.Null(result.Snapshot.SpeakersDetectedAtUtc);
        Assert.Null(result.Snapshot.TranscriptPath);
    }

    [Fact]
    public async Task ValidateArtifacts_MissingVocalsStem_ClearsVocalSeparationArtifacts()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var (_, ambiancePath) = await SessionSemanticsIntegrityFixture.WriteStemPairAsync(_dir, mediaPath);
        var missingVocalsPath = Path.Combine(_dir, "missing-vocals.wav");
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.MediaLoaded,
            IngestedMediaPath = mediaPath,
            VocalsAudioPath = missingVocalsPath,
            AmbianceAudioPath = ambiancePath,
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);

        Assert.Contains("vocal_separation", result.ClearedArtifacts);
        Assert.Null(result.Snapshot.VocalsAudioPath);
        Assert.Null(result.Snapshot.AmbianceAudioPath);
    }

    [Fact]
    public async Task ValidateArtifacts_MissingVocalsStem_WithDiarizationMarker_RecordsDiarization()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var (_, ambiancePath) = await SessionSemanticsIntegrityFixture.WriteStemPairAsync(_dir, mediaPath);
        var now = DateTimeOffset.UtcNow;
        var missingVocalsPath = Path.Combine(_dir, "missing-vocals.wav");
        var snap = WorkflowSessionSnapshot.CreateNew(now) with
        {
            Stage = SessionWorkflowStage.MediaLoaded,
            IngestedMediaPath = mediaPath,
            VocalsAudioPath = missingVocalsPath,
            AmbianceAudioPath = ambiancePath,
            DiarizationProvider = ProviderNames.NemoLocal,
            SpeakersDetectedAtUtc = now,
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);

        Assert.Contains("diarization", result.ClearedArtifacts);
        Assert.Null(result.Snapshot.VocalsAudioPath);
        Assert.Null(result.Snapshot.AmbianceAudioPath);
        Assert.Null(result.Snapshot.DiarizationProvider);
        Assert.Null(result.Snapshot.SpeakersDetectedAtUtc);
    }

    [Fact]
    public async Task ValidateArtifacts_MissingInstrumentalStem_WithNoVocalsPath_ClearsVocalSeparationArtifacts()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var missingInstrumentalPath = Path.Combine(_dir, "missing-instrumental.wav");
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.MediaLoaded,
            IngestedMediaPath = mediaPath,
            VocalsAudioPath = null,
            AmbianceAudioPath = missingInstrumentalPath,
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);

        Assert.Contains("vocal_separation", result.ClearedArtifacts);
        Assert.Null(result.Snapshot.VocalsAudioPath);
        Assert.Null(result.Snapshot.AmbianceAudioPath);
    }

    [Fact]
    public async Task ValidateArtifacts_MissingVocalsStem_AtTranscribedStage_DegradesToMediaLoaded()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var missingVocalsPath = Path.Combine(_dir, "missing-vocals.wav");
        var snap = template with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            VocalsAudioPath = missingVocalsPath,
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);

        Assert.Contains("transcription", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.MediaLoaded, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.VocalsAudioPath);
        Assert.Null(result.Snapshot.TranscriptPath);
        Assert.Null(result.Snapshot.SourceLanguage);
    }

    [Fact]
    public async Task ValidateArtifacts_InvalidTranscriptWithValidStems_DegradesToMediaLoadedAndRecordsStemClear()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var (vocalsPath, ambiancePath) = await SessionSemanticsIntegrityFixture.WriteStemPairAsync(_dir, mediaPath);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            VocalsAudioPath = vocalsPath,
            AmbianceAudioPath = ambiancePath,
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        await File.WriteAllTextAsync(transcriptPath, "{ not json }");

        var snap = template with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            SourceLanguage = "es",
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);

        Assert.Contains("vocal_separation", result.ClearedArtifacts);
        Assert.Contains("transcription", result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.MediaLoaded, result.Snapshot.Stage);
        Assert.Null(result.Snapshot.VocalsAudioPath);
        Assert.Null(result.Snapshot.AmbianceAudioPath);
        Assert.Null(result.Snapshot.TranscriptPath);
        Assert.Null(result.Snapshot.SourceLanguage);
    }

    [Fact]
    public async Task ValidateArtifacts_MissingMedia_DegradesToFoundation()
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
    public async Task ComputeInvalidation_TranscribedStageNoChange_ReturnsNone()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var snap = template with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
        };
        var settings = new AppSettings
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        };
        Assert.Equal(PipelineInvalidation.None, SessionSnapshotSemantics.ComputeInvalidation(snap, settings));
    }

    [Fact]
    public async Task ComputeInvalidation_TranscribedStageModelChanged_ReturnsTranscription()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var snap = template with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
        };
        var settings = new AppSettings
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "large-v3",
        };
        Assert.Equal(PipelineInvalidation.Transcription, SessionSnapshotSemantics.ComputeInvalidation(snap, settings));
    }

    [Fact]
    public async Task ComputeInvalidation_TranscribedStage_VocalSeparationToggleChanged_ReturnsTranscription()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var (vocalsPath, ambiancePath) = await SessionSemanticsIntegrityFixture.WriteStemPairAsync(_dir, mediaPath);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            VocalsAudioPath = vocalsPath,
            AmbianceAudioPath = ambiancePath,
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var snap = template with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
        };
        var settings = new AppSettings
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            VocalSeparationEnabled = false,
        };

        Assert.Equal(PipelineInvalidation.Transcription, SessionSnapshotSemantics.ComputeInvalidation(snap, settings));
    }

    [Fact]
    public async Task ComputeInvalidation_TtsStageOnlyTtsChanged_ReturnsTts()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            TargetLanguage = "en",
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "en-US-JennyNeural",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var translationPath = await SessionSemanticsIntegrityFixture.WriteTranslationAsync(_dir, transcriptPath, template);
        var snapForTts = template with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
        };
        var (ttsPath, segmentsDir, segPaths) =
            await SessionSemanticsIntegrityFixture.WriteTtsBundleAsync(_dir, translationPath, snapForTts);
        var snap = snapForTts with
        {
            TtsPath = ttsPath,
            TtsSegmentsPath = segmentsDir,
            TtsSegmentAudioPaths = segPaths,
        };
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
            TtsVoice = "en-US-AriaNeural",
        };
        Assert.Equal(PipelineInvalidation.Tts, SessionSnapshotSemantics.ComputeInvalidation(snap, settings));
    }

    [Fact]
    public async Task ComputeInvalidation_TtsStageTargetLanguageChanged_ReturnsTranslation()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            TargetLanguage = "en",
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "en-US-JennyNeural",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        var translationPath = await SessionSemanticsIntegrityFixture.WriteTranslationAsync(_dir, transcriptPath, template);
        var snapForTts = template with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
        };
        var (ttsPath, segmentsDir, segPaths) =
            await SessionSemanticsIntegrityFixture.WriteTtsBundleAsync(_dir, translationPath, snapForTts);
        var snap = snapForTts with
        {
            TtsPath = ttsPath,
            TtsSegmentsPath = segmentsDir,
            TtsSegmentAudioPaths = segPaths,
        };
        var settings = new AppSettings
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.Deepl,
            TranslationModel = "default",
            TargetLanguage = "fr",
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
