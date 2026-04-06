using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Tests for <see cref="SessionArtifactReader"/> — file-backed transcript/translation reading
/// and segment state building.
/// </summary>
public sealed class SessionArtifactReaderTests : IDisposable
{
    private readonly string _dir;
    private readonly SessionArtifactReader _reader;

    public SessionArtifactReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-artifact-reader-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _reader = new SessionArtifactReader();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    private string WriteTranscript(IEnumerable<(double start, double end, string text, string? speakerId)> segments)
    {
        var segs = new System.Text.Json.Nodes.JsonArray();
        foreach (var (start, end, text, speakerId) in segments)
        {
            var obj = new System.Text.Json.Nodes.JsonObject
            {
                ["start"] = start,
                ["end"] = end,
                ["text"] = text,
            };
            if (speakerId is not null)
                obj["speakerId"] = speakerId;
            segs.Add(obj);
        }

        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["language"] = "es",
            ["language_probability"] = 0.99,
            ["segments"] = segs,
        };

        var path = Path.Combine(_dir, $"transcript-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    private string WriteTranslation(
        string sourceLang,
        string targetLang,
        IEnumerable<(string id, double start, double end, string text, string translatedText, string? speakerId)> segments)
    {
        var segs = new System.Text.Json.Nodes.JsonArray();
        foreach (var (id, start, end, text, translatedText, speakerId) in segments)
        {
            var obj = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = id,
                ["start"] = start,
                ["end"] = end,
                ["text"] = text,
                ["translatedText"] = translatedText,
            };
            if (speakerId is not null)
                obj["speakerId"] = speakerId;
            segs.Add(obj);
        }

        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["sourceLanguage"] = sourceLang,
            ["targetLanguage"] = targetLang,
            ["segments"] = segs,
        };

        var path = Path.Combine(_dir, $"translation-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    // ── BuildWorkflowSegmentsAsync — no transcript ────────────────────────────

    [Fact]
    public async Task BuildWorkflowSegmentsAsync_NoTranscriptPath_ReturnsEmpty()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        var result = await _reader.BuildWorkflowSegmentsAsync(snap);
        Assert.Empty(result);
    }

    [Fact]
    public async Task BuildWorkflowSegmentsAsync_MissingTranscriptFile_ReturnsEmpty()
    {
        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptPath = Path.Combine(_dir, "nonexistent.json"),
        };
        var result = await _reader.BuildWorkflowSegmentsAsync(snap);
        Assert.Empty(result);
    }

    // ── BuildWorkflowSegmentsAsync — transcript only ──────────────────────────

    [Fact]
    public async Task BuildWorkflowSegmentsAsync_TranscriptOnly_ReturnsSegmentsWithNoTranslation()
    {
        var transcriptPath = WriteTranscript(
        [
            (0.0, 1.5, "Hola mundo", null),
            (1.5, 3.0, "Cómo estás", null),
        ]);

        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptPath = transcriptPath,
        };

        var result = await _reader.BuildWorkflowSegmentsAsync(snap);
        Assert.Equal(2, result.Count);
        Assert.False(result[0].HasTranslation);
        Assert.Null(result[0].TranslatedText);
        Assert.False(result[0].HasTtsAudio);
    }

    [Fact]
    public async Task BuildWorkflowSegmentsAsync_TranscriptOnly_SegmentIdsMatchSegmentIdFormat()
    {
        var transcriptPath = WriteTranscript(
        [
            (0.0, 1.5, "First segment", null),
            (3.68, 5.0, "Second segment", null),
        ]);

        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptPath = transcriptPath,
        };

        var result = await _reader.BuildWorkflowSegmentsAsync(snap);
        Assert.Equal("segment_0.0", result[0].SegmentId);
        Assert.Equal("segment_3.68", result[1].SegmentId);
    }

    [Fact]
    public async Task BuildWorkflowSegmentsAsync_TranscriptWithSpeakerId_PreservesSpeakerId()
    {
        var transcriptPath = WriteTranscript(
        [
            (0.0, 1.5, "Hello", "spk_01"),
        ]);

        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptPath = transcriptPath,
        };

        var result = await _reader.BuildWorkflowSegmentsAsync(snap);
        Assert.Equal("spk_01", result[0].SpeakerId);
    }

    // ── BuildWorkflowSegmentsAsync — with translation ─────────────────────────

    [Fact]
    public async Task BuildWorkflowSegmentsAsync_WithTranslation_PopulatesTranslatedText()
    {
        var transcriptPath = WriteTranscript(
        [
            (0.0, 1.5, "Hola mundo", null),
        ]);
        var translationPath = WriteTranslation("es", "en",
        [
            ("segment_0.0", 0.0, 1.5, "Hola mundo", "Hello world", null),
        ]);

        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
        };

        var result = await _reader.BuildWorkflowSegmentsAsync(snap);
        Assert.Single(result);
        Assert.True(result[0].HasTranslation);
        Assert.Equal("Hello world", result[0].TranslatedText);
    }

    [Fact]
    public async Task BuildWorkflowSegmentsAsync_MissingTranslationFile_NoTranslationPopulated()
    {
        var transcriptPath = WriteTranscript(
        [
            (0.0, 1.5, "Hola", null),
        ]);

        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptPath = transcriptPath,
            TranslationPath = Path.Combine(_dir, "nonexistent-translation.json"),
        };

        var result = await _reader.BuildWorkflowSegmentsAsync(snap);
        Assert.Single(result);
        Assert.False(result[0].HasTranslation);
    }

    [Fact]
    public async Task BuildWorkflowSegmentsAsync_WithSpeakerVoiceAssignment_PopulatesAssignedVoice()
    {
        var transcriptPath = WriteTranscript(
        [
            (0.0, 1.5, "Hello", "spk_01"),
        ]);

        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptPath = transcriptPath,
            SpeakerVoiceAssignments = new Dictionary<string, string>
            {
                ["spk_01"] = "en-US-JennyNeural",
            },
        };

        var result = await _reader.BuildWorkflowSegmentsAsync(snap);
        Assert.Equal("en-US-JennyNeural", result[0].AssignedVoice);
    }

    [Fact]
    public async Task BuildWorkflowSegmentsAsync_TtsPathsDict_MarksTtsAudio()
    {
        var transcriptPath = WriteTranscript(
        [
            (0.0, 1.5, "Hola", null),
        ]);

        // Create a fake TTS file so File.Exists returns true
        var ttsSegPath = Path.Combine(_dir, "segment_0.0.mp3");
        File.WriteAllText(ttsSegPath, "audio");

        var snap = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            TranscriptPath = transcriptPath,
            TtsSegmentAudioPaths = new Dictionary<string, string>
            {
                ["segment_0.0"] = ttsSegPath,
            },
        };

        var result = await _reader.BuildWorkflowSegmentsAsync(snap);
        Assert.True(result[0].HasTtsAudio);
    }

    // ── GetTranslatedTextAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetTranslatedTextAsync_FoundSegment_ReturnsTranslatedText()
    {
        var translationPath = WriteTranslation("es", "en",
        [
            ("segment_0.0", 0.0, 1.5, "Hola", "Hello", null),
        ]);

        var text = await _reader.GetTranslatedTextAsync(translationPath, "segment_0.0");
        Assert.Equal("Hello", text);
    }

    [Fact]
    public async Task GetTranslatedTextAsync_MissingSegment_Throws()
    {
        var translationPath = WriteTranslation("es", "en",
        [
            ("segment_0.0", 0.0, 1.5, "Hola", "Hello", null),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _reader.GetTranslatedTextAsync(translationPath, "segment_99.0"));
    }

    // ── GetSourceTextAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetSourceTextAsync_FoundSegment_ReturnsSourceText()
    {
        var translationPath = WriteTranslation("es", "en",
        [
            ("segment_0.0", 0.0, 1.5, "Hola mundo", "Hello world", null),
        ]);

        var text = await _reader.GetSourceTextAsync(translationPath, "segment_0.0");
        Assert.Equal("Hola mundo", text);
    }

    [Fact]
    public async Task GetSourceTextAsync_MissingSegment_Throws()
    {
        var translationPath = WriteTranslation("es", "en",
        [
            ("segment_0.0", 0.0, 1.5, "Hola", "Hello", null),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _reader.GetSourceTextAsync(translationPath, "segment_99.0"));
    }
}
