using System;
using System.IO;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class ArtifactIntegrityValidatorTests : IDisposable
{
    private readonly string _dir;

    public ArtifactIntegrityValidatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-validator-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task ValidateTranscript_CorruptedJson_ReturnsFalseAndError()
    {
        var mediaPath = await SessionSemanticsIntegrityFixture.WriteMediaCopyAsync(_dir);
        var template = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "base",
        };
        var transcriptPath = await SessionSemanticsIntegrityFixture.WriteTranscriptAsync(_dir, mediaPath, template);
        await File.WriteAllTextAsync(transcriptPath, "{ not json }");

        var snapshot = template with
        {
            Stage = SessionWorkflowStage.Transcribed,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
        };

        var valid = ArtifactIntegrityValidator.ValidateTranscript(snapshot, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("Transcript artifact was unreadable", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateTranslation_CorruptedJson_ReturnsFalseAndError()
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
        await File.WriteAllTextAsync(translationPath, "{ not json }");

        var snapshot = template with
        {
            Stage = SessionWorkflowStage.Translated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
        };

        var valid = ArtifactIntegrityValidator.ValidateTranslation(snapshot, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("Translation artifact was unreadable", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateTranslation_CorruptedTranscriptJson_ReturnsFalseAndError()
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
        await File.WriteAllTextAsync(transcriptPath, "{ not json }");

        var snapshot = template with
        {
            Stage = SessionWorkflowStage.Translated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
        };

        var valid = ArtifactIntegrityValidator.ValidateTranslation(snapshot, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("Transcript artifact was unreadable", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateTts_CorruptedTranslationJson_ReturnsFalseAndError()
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
        var ttsSnapshot = template with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            IngestedMediaPath = mediaPath,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
        };
        var (ttsPath, segmentsDir, segmentPaths) =
            await SessionSemanticsIntegrityFixture.WriteTtsBundleAsync(_dir, translationPath, ttsSnapshot);
        await File.WriteAllTextAsync(translationPath, "{ not json }");

        var snapshot = ttsSnapshot with
        {
            TtsPath = ttsPath,
            TtsSegmentsPath = segmentsDir,
            TtsSegmentAudioPaths = segmentPaths,
        };

        var valid = ArtifactIntegrityValidator.ValidateTts(snapshot, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("Translation artifact was unreadable", error, StringComparison.OrdinalIgnoreCase);
    }
}
