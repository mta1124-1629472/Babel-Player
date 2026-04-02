using System;
using System.Collections.Generic;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;

namespace BabelPlayer.Tests;

/// <summary>
/// Tests for domain model records, enums, StageContracts records, and PipelineProviderException.
/// </summary>
public sealed class ModelTests
{
    // ── WorkflowSessionSnapshot.CreateNew ─────────────────────────────────────

    [Fact]
    public void CreateNew_StageIsFoundation()
    {
        var s = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        Assert.Equal(SessionWorkflowStage.Foundation, s.Stage);
    }

    [Fact]
    public void CreateNew_SessionIdIsNonEmpty()
    {
        var s = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        Assert.NotEqual(Guid.Empty, s.SessionId);
    }

    [Fact]
    public void CreateNew_TwoCallsProduceDifferentIds()
    {
        var a = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        var b = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        Assert.NotEqual(a.SessionId, b.SessionId);
    }

    [Fact]
    public void CreateNew_TimestampsMatchSuppliedValue()
    {
        var now = DateTimeOffset.Parse("2025-06-15T10:00:00Z");
        var s = WorkflowSessionSnapshot.CreateNew(now);
        Assert.Equal(now, s.CreatedAtUtc);
        Assert.Equal(now, s.LastUpdatedAtUtc);
    }

    [Fact]
    public void CreateNew_NullablePathFieldsAreNull()
    {
        var s = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        Assert.Null(s.SourceMediaPath);
        Assert.Null(s.IngestedMediaPath);
        Assert.Null(s.TranscriptPath);
        Assert.Null(s.TranslationPath);
        Assert.Null(s.SourceLanguage);
        Assert.Null(s.TargetLanguage);
        Assert.Null(s.TtsPath);
        Assert.Null(s.TtsVoice);
    }

    [Fact]
    public void CreateNew_StatusMessageIsNotEmpty()
    {
        var s = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        Assert.False(string.IsNullOrWhiteSpace(s.StatusMessage));
    }

    // ── WorkflowSessionSnapshot record semantics ──────────────────────────────

    [Fact]
    public void WithExpression_PreservesUnchangedFields()
    {
        var original = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        var updated = original with { Stage = SessionWorkflowStage.MediaLoaded, SourceMediaPath = "/video.mp4" };

        Assert.Equal(original.SessionId, updated.SessionId);
        Assert.Equal(original.CreatedAtUtc, updated.CreatedAtUtc);
        Assert.Equal(SessionWorkflowStage.MediaLoaded, updated.Stage);
        Assert.Equal("/video.mp4", updated.SourceMediaPath);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var a = new WorkflowSessionSnapshot(id, SessionWorkflowStage.Foundation, now, now, "ready");
        var b = new WorkflowSessionSnapshot(id, SessionWorkflowStage.Foundation, now, now, "ready");
        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentId_NotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new WorkflowSessionSnapshot(Guid.NewGuid(), SessionWorkflowStage.Foundation, now, now, "ready");
        var b = new WorkflowSessionSnapshot(Guid.NewGuid(), SessionWorkflowStage.Foundation, now, now, "ready");
        Assert.NotEqual(a, b);
    }

    // ── SessionWorkflowStage ordering ─────────────────────────────────────────

    [Fact]
    public void SessionWorkflowStage_FoundationIsLowest()
    {
        Assert.True((int)SessionWorkflowStage.Foundation < (int)SessionWorkflowStage.MediaLoaded);
    }

    [Fact]
    public void SessionWorkflowStage_TtsGeneratedIsHighest()
    {
        Assert.True((int)SessionWorkflowStage.TtsGenerated > (int)SessionWorkflowStage.Translated);
        Assert.True((int)SessionWorkflowStage.TtsGenerated > (int)SessionWorkflowStage.Transcribed);
        Assert.True((int)SessionWorkflowStage.TtsGenerated > (int)SessionWorkflowStage.MediaLoaded);
        Assert.True((int)SessionWorkflowStage.TtsGenerated > (int)SessionWorkflowStage.Foundation);
    }

    [Fact]
    public void SessionWorkflowStage_StagesAreOrderedCorrectly()
    {
        Assert.True(SessionWorkflowStage.Foundation < SessionWorkflowStage.MediaLoaded);
        Assert.True(SessionWorkflowStage.MediaLoaded < SessionWorkflowStage.Transcribed);
        Assert.True(SessionWorkflowStage.Transcribed < SessionWorkflowStage.Translated);
        Assert.True(SessionWorkflowStage.Translated < SessionWorkflowStage.TtsGenerated);
    }

    // ── PipelineInvalidation enum ─────────────────────────────────────────────

    [Fact]
    public void PipelineInvalidation_NoneIsZero()
    {
        Assert.Equal(0, (int)PipelineInvalidation.None);
    }

    [Fact]
    public void PipelineInvalidation_HasExpectedValues()
    {
        var values = (PipelineInvalidation[])Enum.GetValues(typeof(PipelineInvalidation));
        Assert.Contains(PipelineInvalidation.None, values);
        Assert.Contains(PipelineInvalidation.Tts, values);
        Assert.Contains(PipelineInvalidation.Translation, values);
        Assert.Contains(PipelineInvalidation.Transcription, values);
    }

    // ── RecentSessionEntry record ─────────────────────────────────────────────

    [Fact]
    public void RecentSessionEntry_ConstructsCorrectly()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var entry = new RecentSessionEntry(id, "/path/to/video.mp4", "video.mp4", SessionWorkflowStage.Translated, now);

        Assert.Equal(id, entry.SessionId);
        Assert.Equal("/path/to/video.mp4", entry.SourceMediaPath);
        Assert.Equal("video.mp4", entry.SourceMediaFileName);
        Assert.Equal(SessionWorkflowStage.Translated, entry.Stage);
        Assert.Equal(now, entry.LastUpdatedAtUtc);
    }

    [Fact]
    public void RecentSessionEntry_RecordEquality_SameValues_AreEqual()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var a = new RecentSessionEntry(id, "/v.mp4", "v.mp4", SessionWorkflowStage.MediaLoaded, now);
        var b = new RecentSessionEntry(id, "/v.mp4", "v.mp4", SessionWorkflowStage.MediaLoaded, now);
        Assert.Equal(a, b);
    }

    // ── VideoPlaybackOptions record ───────────────────────────────────────────

    [Fact]
    public void VideoPlaybackOptions_DefaultHwdecIsAuto()
    {
        var opts = new VideoPlaybackOptions();
        Assert.Equal("auto", opts.HwdecMode);
    }

    [Fact]
    public void VideoPlaybackOptions_DefaultGpuApiIsAuto()
    {
        var opts = new VideoPlaybackOptions();
        Assert.Equal("auto", opts.GpuApi);
    }

    [Fact]
    public void VideoPlaybackOptions_CustomValues_ArePersisted()
    {
        var opts = new VideoPlaybackOptions("d3d11va", "d3d11");
        Assert.Equal("d3d11va", opts.HwdecMode);
        Assert.Equal("d3d11", opts.GpuApi);
    }

    // ── ProviderDescriptor defaults ───────────────────────────────────────────

    [Fact]
    public void ProviderDescriptor_DefaultIsImplemented_IsTrue()
    {
        var desc = new ProviderDescriptor("test-id", "Test", false, null, []);
        Assert.True(desc.IsImplemented);
    }

    [Fact]
    public void ProviderDescriptor_ExplicitIsImplementedFalse()
    {
        var desc = new ProviderDescriptor("test-id", "Test", false, null, [], IsImplemented: false);
        Assert.False(desc.IsImplemented);
    }

    // ── ProviderReadiness static Ready ───────────────────────────────────────

    [Fact]
    public void ProviderReadiness_Ready_IsReady()
    {
        Assert.True(ProviderReadiness.Ready.IsReady);
    }

    [Fact]
    public void ProviderReadiness_Ready_BlockingReasonIsNull()
    {
        Assert.Null(ProviderReadiness.Ready.BlockingReason);
    }

    [Fact]
    public void ProviderReadiness_Ready_RequiresModelDownloadIsFalse()
    {
        Assert.False(ProviderReadiness.Ready.RequiresModelDownload);
    }

    [Fact]
    public void ProviderReadiness_NotReady_HasBlockingReason()
    {
        var r = new ProviderReadiness(false, "Missing API key.");
        Assert.False(r.IsReady);
        Assert.Equal("Missing API key.", r.BlockingReason);
    }

    // ── PipelineProviderException ─────────────────────────────────────────────

    [Fact]
    public void PipelineProviderException_IsInvalidOperationException()
    {
        var ex = new PipelineProviderException("test message");
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public void PipelineProviderException_CarriesMessage()
    {
        var ex = new PipelineProviderException("the provider is not supported");
        Assert.Equal("the provider is not supported", ex.Message);
    }

    // ── StageContracts records ────────────────────────────────────────────────

    [Fact]
    public void TranscriptionRequest_ConstructsCorrectly()
    {
        var req = new TranscriptionRequest("/audio.mp3", "/out.json", "base", "es", "int8", 8, 2);
        Assert.Equal("/audio.mp3", req.SourceAudioPath);
        Assert.Equal("/out.json", req.OutputJsonPath);
        Assert.Equal("base", req.ModelName);
        Assert.Equal("es", req.LanguageHint);
        Assert.Equal("int8", req.CpuComputeType);
        Assert.Equal(8, req.CpuThreads);
        Assert.Equal(2, req.NumWorkers);
    }

    [Fact]
    public void TranscriptionRequest_LanguageHintDefaultsToNull()
    {
        var req = new TranscriptionRequest("/audio.mp3", "/out.json", "base");
        Assert.Null(req.LanguageHint);
        Assert.Equal("int8", req.CpuComputeType);
        Assert.Equal(0, req.CpuThreads);
        Assert.Equal(1, req.NumWorkers);
    }

    [Fact]
    public void AppSettings_TranscriptionCpuDefaults_AreSafe()
    {
        var settings = new Babel.Player.Services.Settings.AppSettings();
        Assert.Equal("int8", settings.TranscriptionCpuComputeType);
        Assert.Equal(0, settings.TranscriptionCpuThreads);
        Assert.Equal(1, settings.TranscriptionNumWorkers);
    }

    [Fact]
    public void TranscriptionResult_SuccessFlag_True()
    {
        var result = new TranscriptionResult(true, [], "en", 0.99, null);
        Assert.True(result.Success);
        Assert.Equal("en", result.Language);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void TranscriptionResult_FailureFlag_HasErrorMessage()
    {
        var result = new TranscriptionResult(false, [], "unknown", 0.0, "subprocess crashed");
        Assert.False(result.Success);
        Assert.Equal("subprocess crashed", result.ErrorMessage);
    }

    [Fact]
    public void TranscriptSegment_ConstructsCorrectly()
    {
        var seg = new TranscriptSegment(1.5, 3.0, "hello world", "spk_01");
        Assert.Equal(1.5, seg.StartSeconds);
        Assert.Equal(3.0, seg.EndSeconds);
        Assert.Equal("hello world", seg.Text);
        Assert.Equal("spk_01", seg.SpeakerId);
    }

    [Fact]
    public void TranslationRequest_ConstructsCorrectly()
    {
        var req = new TranslationRequest("/transcript.json", "/out.json", "es", "en", "default");
        Assert.Equal("/transcript.json", req.TranscriptJsonPath);
        Assert.Equal("en", req.TargetLanguage);
        Assert.Equal("es", req.SourceLanguage);
    }

    [Fact]
    public void TranslationResult_SuccessFlag()
    {
        var result = new TranslationResult(true, [], "es", "en", null);
        Assert.True(result.Success);
        Assert.Equal("es", result.SourceLanguage);
        Assert.Equal("en", result.TargetLanguage);
    }

    [Fact]
    public void WorkflowSegmentState_ConstructsWithSpeakerMetadata()
    {
        var segment = new WorkflowSegmentState(
            "segment_0.0",
            0,
            1,
            "hola",
            true,
            "hello",
            true,
            "spk_01",
            "en-US-AriaNeural",
            true);

        Assert.Equal("spk_01", segment.SpeakerId);
        Assert.Equal("en-US-AriaNeural", segment.AssignedVoice);
        Assert.True(segment.HasReferenceAudio);
    }

    [Fact]
    public void TtsResult_SuccessFlag()
    {
        var result = new TtsResult(true, "/output.mp3", "en-US-AriaNeural", 12345, null);
        Assert.True(result.Success);
        Assert.Equal("/output.mp3", result.AudioPath);
        Assert.Equal(12345, result.FileSizeBytes);
    }

    [Fact]
    public void TtsRequest_ConstructsCorrectly()
    {
        var req = new TtsRequest("/translation.json", "/audio.mp3", "en-US-AriaNeural");
        Assert.Equal("/translation.json", req.TranslationJsonPath);
        Assert.Equal("/audio.mp3", req.OutputAudioPath);
        Assert.Equal("en-US-AriaNeural", req.VoiceName);
    }

    // ── ProviderNames / CredentialKeys constants ──────────────────────────────

    [Fact]
    public void ProviderNames_AllConstantsAreNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(ProviderNames.FasterWhisper));
        Assert.False(string.IsNullOrWhiteSpace(ProviderNames.GoogleTranslateFree));
        Assert.False(string.IsNullOrWhiteSpace(ProviderNames.EdgeTts));
        Assert.False(string.IsNullOrWhiteSpace(ProviderNames.Piper));
        Assert.False(string.IsNullOrWhiteSpace(ProviderNames.Nllb200));
        Assert.False(string.IsNullOrWhiteSpace(ProviderNames.ContainerizedService));
    }

    [Fact]
    public void CredentialKeys_AllConstantsAreNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(CredentialKeys.OpenAi));
        Assert.False(string.IsNullOrWhiteSpace(CredentialKeys.GoogleAi));
        Assert.False(string.IsNullOrWhiteSpace(CredentialKeys.ElevenLabs));
        Assert.False(string.IsNullOrWhiteSpace(CredentialKeys.Deepl));
    }

    [Fact]
    public void ProviderNames_AllConstantsAreDistinct()
    {
        var names = new[]
        {
            ProviderNames.FasterWhisper,
            ProviderNames.OpenAiWhisperApi,
            ProviderNames.GoogleStt,
            ProviderNames.GoogleTranslateFree,
            ProviderNames.Nllb200,
            ProviderNames.Deepl,
            ProviderNames.OpenAi,
            ProviderNames.EdgeTts,
            ProviderNames.Piper,
            ProviderNames.ElevenLabs,
            ProviderNames.GoogleCloudTts,
            ProviderNames.OpenAiTts,
            ProviderNames.ContainerizedService,
        };
        Assert.Equal(names.Length, new System.Collections.Generic.HashSet<string>(names).Count);
    }
}
