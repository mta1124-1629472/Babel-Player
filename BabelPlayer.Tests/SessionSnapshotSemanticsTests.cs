using System;
using System.IO;
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
        var mediaPath = WriteFile("video.mp4");
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
    public void ResolveArtifactStage_Transcribed_WithBothFiles_ReturnsTranscribed()
    {
        var mediaPath = WriteFile("video.mp4");
        var transcriptPath = WriteFile("transcript.json");
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
        };
        Assert.Equal(SessionWorkflowStage.Transcribed, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public void ResolveArtifactStage_Transcribed_MissingTranscript_ReturnsMediaLoaded()
    {
        var mediaPath = WriteFile("video.mp4");
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = Path.Combine(_dir, "missing-transcript.json"),
        };
        Assert.Equal(SessionWorkflowStage.MediaLoaded, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public void ResolveArtifactStage_TtsGenerated_WithAllFiles_ReturnsTtsGenerated()
    {
        var mediaPath = WriteFile("video.mp4");
        var transcriptPath = WriteFile("transcript.json");
        var translationPath = WriteFile("translation.json");
        var ttsPath = WriteFile("tts.mp3");
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
            TtsPath = ttsPath,
        };
        Assert.Equal(SessionWorkflowStage.TtsGenerated, SessionSnapshotSemantics.ResolveArtifactStage(snap));
    }

    [Fact]
    public void ResolveArtifactStage_TtsGenerated_MissingTtsFile_ReturnsTranslated()
    {
        var mediaPath = WriteFile("video.mp4");
        var transcriptPath = WriteFile("transcript.json");
        var translationPath = WriteFile("translation.json");
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
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
    public void ValidateArtifacts_TtsGeneratedWithAllFiles_NoClearedArtifacts()
    {
        var mediaPath = WriteFile("video.mp4");
        var transcriptPath = WriteFile("transcript.json");
        var translationPath = WriteFile("translation.json");
        var ttsPath = WriteFile("tts.mp3");
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
            TtsPath = ttsPath,
        };

        var result = SessionSnapshotSemantics.ValidateArtifacts(snap);
        Assert.Empty(result.ClearedArtifacts);
        Assert.Equal(SessionWorkflowStage.TtsGenerated, result.Snapshot.Stage);
    }

    [Fact]
    public void ValidateArtifacts_MissingTts_DegradesToTranslated()
    {
        var mediaPath = WriteFile("video.mp4");
        var transcriptPath = WriteFile("transcript.json");
        var translationPath = WriteFile("translation.json");
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
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
    public void ValidateArtifacts_MissingTranslation_DegradesToTranscribed()
    {
        var mediaPath = WriteFile("video.mp4");
        var transcriptPath = WriteFile("transcript.json");
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
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
    public void ValidateArtifacts_MissingTranscript_DegradesToMediaLoaded()
    {
        var mediaPath = WriteFile("video.mp4");
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
        var transcriptPath = WriteFile("transcript.json");
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = WriteFile("video.mp4"),
            TranscriptPath = transcriptPath,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        };
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
        var transcriptPath = WriteFile("transcript.json");
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = WriteFile("video.mp4"),
            TranscriptPath = transcriptPath,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        };
        var settings = new AppSettings
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "large-v3",
        };
        Assert.Equal(PipelineInvalidation.Transcription, SessionSnapshotSemantics.ComputeInvalidation(snap, settings));
    }

    [Fact]
    public void ComputeInvalidation_TtsStageOnlyTtsChanged_ReturnsTts()
    {
        var ttsPath = WriteFile("tts.mp3");
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            IngestedMediaPath = WriteFile("video.mp4"),
            TranscriptPath = WriteFile("transcript.json"),
            TranslationPath = WriteFile("translation.json"),
            TtsPath = ttsPath,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.GoogleTranslateFree,
            TranslationModel = "default",
            TargetLanguage = "en",
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "en-US-JennyNeural",
        };
        var settings = new AppSettings
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.GoogleTranslateFree,
            TranslationModel = "default",
            TargetLanguage = "en",
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "en-US-AriaNeural", // changed voice
        };
        Assert.Equal(PipelineInvalidation.Tts, SessionSnapshotSemantics.ComputeInvalidation(snap, settings));
    }

    [Fact]
    public void ComputeInvalidation_TtsStageTargetLanguageChanged_ReturnsTranslation()
    {
        var ttsPath = WriteFile("tts.mp3");
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            IngestedMediaPath = WriteFile("video.mp4"),
            TranscriptPath = WriteFile("transcript.json"),
            TranslationPath = WriteFile("translation.json"),
            TtsPath = ttsPath,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.GoogleTranslateFree,
            TranslationModel = "default",
            TargetLanguage = "en",
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "en-US-JennyNeural",
        };
        var settings = new AppSettings
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
            TranslationProvider = ProviderNames.GoogleTranslateFree,
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
    }

    // ── ClearTranscriptionOutputs ─────────────────────────────────────────────

    [Fact]
    public void ClearTranscriptionOutputs_ClearsAllDownstreamFields()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptPath = "/some/transcript.json",
            SourceLanguage = "es",
            TranslationPath = "/some/translation.json",
            TargetLanguage = "en",
            TtsPath = "/some/tts.mp3",
        };

        var result = SessionSnapshotSemantics.ClearTranscriptionOutputs(snap);
        Assert.Null(result.TranscriptPath);
        Assert.Null(result.SourceLanguage);
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
            TranslationProvider = ProviderNames.GoogleTranslateFree,
            TtsProvider = ProviderNames.EdgeTts,
            SourceLanguage = "es",
            TargetLanguage = "en",
        };

        var desc = SessionSnapshotSemantics.DescribeSessionProvenance(snap);
        Assert.Contains("Translated", desc);
        Assert.Contains(ProviderNames.FasterWhisper, desc);
        Assert.Contains(ProviderNames.GoogleTranslateFree, desc);
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
