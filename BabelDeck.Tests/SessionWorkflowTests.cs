using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Babel.Deck.Models;
using Babel.Deck.Services;

namespace BabelDeck.Tests;

public class SessionWorkflowTests : IDisposable
{
    private readonly string _testStateDir;
    private readonly string _testLogDir;
    private readonly string _testMediaPath;
    private string? _lastStateFilePath;

    public SessionWorkflowTests()
    {
        _testStateDir = Path.Combine(Path.GetTempPath(), $"BabelDeckTest_{Guid.NewGuid():N}");
        _testLogDir = Path.Combine(Path.GetTempPath(), $"BabelDeckLogTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testStateDir);
        Directory.CreateDirectory(_testLogDir);
        
        var mp4Path = Path.Combine(AppContext.BaseDirectory, "test-assets", "video", "sample.mp4");
        
        _testMediaPath = mp4Path;
    }

    private string GetTestLogPath() => Path.Combine(_testLogDir, "test.log");

    [Fact]
    public void LoadMedia_ThenReopen_ReusesArtifact()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);

        coordinator.Initialize();
        Assert.Equal(SessionWorkflowStage.Foundation, coordinator.CurrentSession.Stage);

        Assert.True(File.Exists(_testMediaPath), $"Test media not found: {_testMediaPath}");
        coordinator.LoadMedia(_testMediaPath);

        Assert.Equal(SessionWorkflowStage.MediaLoaded, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.SourceMediaPath);
        Assert.NotNull(coordinator.CurrentSession.IngestedMediaPath);
        Assert.Equal(_testMediaPath, coordinator.CurrentSession.SourceMediaPath);
        Assert.True(File.Exists(coordinator.CurrentSession.IngestedMediaPath), 
            $"Ingested media should exist at: {coordinator.CurrentSession.IngestedMediaPath}");

        var sessionId = coordinator.CurrentSession.SessionId;

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Equal(sessionId, coordinator.CurrentSession.SessionId);
        Assert.Equal(SessionWorkflowStage.MediaLoaded, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.IngestedMediaPath);
        Assert.True(File.Exists(coordinator.CurrentSession.IngestedMediaPath),
            "After reopen, ingested media artifact should still exist");
        Assert.DoesNotContain("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReopenWithMissingArtifact_SurfacesDegradedState()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_missing_artifact.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.True(File.Exists(_testMediaPath));
        coordinator.LoadMedia(_testMediaPath);

        var ingestedPath = coordinator.CurrentSession.IngestedMediaPath;
        Assert.NotNull(ingestedPath);

        File.Delete(ingestedPath);

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Contains("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscribeMedia_ProducesTimedSegments()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_transcribe.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.True(File.Exists(_testMediaPath), $"Test media not found: {_testMediaPath}");
        coordinator.LoadMedia(_testMediaPath);

        await coordinator.TranscribeMediaAsync();

        Assert.Equal(SessionWorkflowStage.Transcribed, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TranscriptPath);
        Assert.True(File.Exists(coordinator.CurrentSession.TranscriptPath), 
            $"Transcript should exist at: {coordinator.CurrentSession.TranscriptPath}");

        var transcriptJson = await File.ReadAllTextAsync(coordinator.CurrentSession.TranscriptPath);
        Assert.NotEmpty(transcriptJson);
        Assert.Contains("segments", transcriptJson);
        
        var transcriptData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(transcriptJson);
        var segments = transcriptData.GetProperty("segments");
        Assert.True(segments.GetArrayLength() > 0, "Transcript should have segments");
    }

    [Fact]
    public async Task TranscribeMedia_ThenReopen_ReusesTranscript()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_transcribe_reopen.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();

        var transcriptPath = coordinator.CurrentSession.TranscriptPath;
        var sessionId = coordinator.CurrentSession.SessionId;

        Assert.NotNull(transcriptPath);
        Assert.True(File.Exists(transcriptPath));

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Equal(sessionId, coordinator.CurrentSession.SessionId);
        Assert.Equal(SessionWorkflowStage.Transcribed, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TranscriptPath);
        Assert.True(File.Exists(coordinator.CurrentSession.TranscriptPath),
            "After reopen, transcript artifact should still exist");
        Assert.DoesNotContain("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReopenWithMissingTranscript_SurfacesDegradedState()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_missing_transcript.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();

        var transcriptPath = coordinator.CurrentSession.TranscriptPath;
        Assert.NotNull(transcriptPath);

        File.Delete(transcriptPath);

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Contains("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranslateTranscript_ProducesTranslatedSegments()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_translate.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();

        Assert.Equal(SessionWorkflowStage.Transcribed, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TranscriptPath);

        await coordinator.TranslateTranscriptAsync("en", "es");

        Assert.Equal(SessionWorkflowStage.Translated, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TranslationPath);
        Assert.NotNull(coordinator.CurrentSession.TargetLanguage);
        Assert.Equal("en", coordinator.CurrentSession.TargetLanguage);
        Assert.True(File.Exists(coordinator.CurrentSession.TranslationPath), 
            $"Translation should exist at: {coordinator.CurrentSession.TranslationPath}");

        var translationJson = await File.ReadAllTextAsync(coordinator.CurrentSession.TranslationPath);
        Assert.NotEmpty(translationJson);
        Assert.Contains("translatedText", translationJson);
        Assert.Contains("en", translationJson);
    }

    [Fact]
    public async Task TranslateTranscript_ThenReopen_ReusesTranslation()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_translate_reopen.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");

        var translationPath = coordinator.CurrentSession.TranslationPath;
        var sessionId = coordinator.CurrentSession.SessionId;

        Assert.NotNull(translationPath);
        Assert.True(File.Exists(translationPath));

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Equal(sessionId, coordinator.CurrentSession.SessionId);
        Assert.Equal(SessionWorkflowStage.Translated, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TranslationPath);
        Assert.True(File.Exists(coordinator.CurrentSession.TranslationPath),
            "After reopen, translation artifact should still exist");
        Assert.DoesNotContain("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReopenWithMissingTranslation_SurfacesDegradedState()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_missing_translation.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");

        var translationPath = coordinator.CurrentSession.TranslationPath;
        Assert.NotNull(translationPath);

        File.Delete(translationPath);

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Contains("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateTts_ProducesAudio()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_tts.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");

        Assert.Equal(SessionWorkflowStage.Translated, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TranslationPath);

        await coordinator.GenerateTtsAsync();

        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TtsPath);
        Assert.NotNull(coordinator.CurrentSession.TtsVoice);
        Assert.True(File.Exists(coordinator.CurrentSession.TtsPath), 
            $"TTS audio should exist at: {coordinator.CurrentSession.TtsPath}");
        
        var fileInfo = new FileInfo(coordinator.CurrentSession.TtsPath);
        Assert.True(fileInfo.Length > 0, "TTS audio should have non-zero size");
    }

    [Fact]
    public async Task GenerateTts_ThenReopen_ReusesAudio()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_tts_reopen.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");
        await coordinator.GenerateTtsAsync();

        var ttsPath = coordinator.CurrentSession.TtsPath;
        var sessionId = coordinator.CurrentSession.SessionId;

        Assert.NotNull(ttsPath);
        Assert.True(File.Exists(ttsPath));

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Equal(sessionId, coordinator.CurrentSession.SessionId);
        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TtsPath);
        Assert.True(File.Exists(coordinator.CurrentSession.TtsPath),
            "After reopen, TTS artifact should still exist");
        Assert.DoesNotContain("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReopenWithMissingTts_SurfacesDegradedState()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_missing_tts.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");
        await coordinator.GenerateTtsAsync();

        var ttsPath = coordinator.CurrentSession.TtsPath;
        Assert.NotNull(ttsPath);

        File.Delete(ttsPath);

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Contains("missing", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testStateDir))
        {
            Directory.Delete(_testStateDir, true);
        }
        if (Directory.Exists(_testLogDir))
        {
            Directory.Delete(_testLogDir, true);
        }
    }

    [Fact]
    public async Task RegenerateSegmentTts_ProducesSingleSegmentAudio()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_regen_seg.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");
        await coordinator.GenerateTtsAsync();

        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TtsSegmentsPath);

        var translationJson = await File.ReadAllTextAsync(coordinator.CurrentSession.TranslationPath!);
        var translationData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(translationJson);
        var segments = translationData.GetProperty("segments");
        var firstSegment = segments[0];
        var segmentId = firstSegment.GetProperty("id").GetString();

        Assert.NotNull(segmentId);

        await coordinator.RegenerateSegmentTtsAsync(segmentId);

        Assert.NotNull(coordinator.CurrentSession.TtsSegmentAudioPaths);
        Assert.True(coordinator.CurrentSession.TtsSegmentAudioPaths!.ContainsKey(segmentId));
        
        var segmentAudioPath = coordinator.CurrentSession.TtsSegmentAudioPaths[segmentId];
        Assert.True(File.Exists(segmentAudioPath), $"Segment audio should exist at: {segmentAudioPath}");
        
        var fileInfo = new FileInfo(segmentAudioPath);
        Assert.True(fileInfo.Length > 0, "Segment audio should have non-zero size");
    }

    [Fact]
    public async Task RegenerateSegmentTts_ThenReopen_PreservesChange()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_regen_seg_reopen.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");
        await coordinator.GenerateTtsAsync();

        var translationJson = await File.ReadAllTextAsync(coordinator.CurrentSession.TranslationPath!);
        var translationData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(translationJson);
        var segments = translationData.GetProperty("segments");
        var segmentId = segments[0].GetProperty("id").GetString();

        await coordinator.RegenerateSegmentTtsAsync(segmentId!);

        var segmentAudioPath = coordinator.CurrentSession.TtsSegmentAudioPaths![segmentId!];
        var sessionId = coordinator.CurrentSession.SessionId;

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Equal(sessionId, coordinator.CurrentSession.SessionId);
        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        Assert.NotNull(coordinator.CurrentSession.TtsSegmentAudioPaths);
        Assert.True(coordinator.CurrentSession.TtsSegmentAudioPaths!.ContainsKey(segmentId));
        Assert.True(File.Exists(coordinator.CurrentSession.TtsSegmentAudioPaths[segmentId]),
            "After reopen, regenerated segment audio should still exist");
    }

    [Fact]
    public async Task RegenerateSegmentTranslation_UpdatesSingleSegment()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_regen_trans.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");

        Assert.NotNull(coordinator.CurrentSession.TranslationPath);

        var translationJsonBefore = await File.ReadAllTextAsync(coordinator.CurrentSession.TranslationPath);
        var dataBefore = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(translationJsonBefore);
        var segmentsBefore = dataBefore.GetProperty("segments");
        var segmentCountBefore = segmentsBefore.GetArrayLength();
        var firstSegmentBefore = segmentsBefore[0];
        var segmentId = firstSegmentBefore.GetProperty("id").GetString();
        var textBefore = firstSegmentBefore.GetProperty("translatedText").GetString();

        Assert.NotNull(segmentId);

        await coordinator.RegenerateSegmentTranslationAsync(segmentId!);

        var translationJsonAfter = await File.ReadAllTextAsync(coordinator.CurrentSession.TranslationPath!);
        var dataAfter = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(translationJsonAfter);
        var segmentsAfter = dataAfter.GetProperty("segments");
        var segmentCountAfter = segmentsAfter.GetArrayLength();

        Assert.Equal(segmentCountBefore, segmentCountAfter);

        var firstSegmentAfter = segmentsAfter[0];
        var textAfter = firstSegmentAfter.GetProperty("translatedText").GetString();

        Assert.NotNull(textAfter);
    }

    [Fact]
    public async Task RegenerateSegmentTranslation_DoesNotModifyOtherSegments()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_regen_trans_other.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");

        var translationJsonBefore = await File.ReadAllTextAsync(coordinator.CurrentSession.TranslationPath);
        var dataBefore = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(translationJsonBefore);
        var segmentsBefore = dataBefore.GetProperty("segments");
        var segmentId = segmentsBefore[0].GetProperty("id").GetString();

        var otherSegmentsBefore = new Dictionary<string, string?>();
        foreach (var seg in segmentsBefore.EnumerateArray())
        {
            var id = seg.GetProperty("id").GetString();
            if (id != segmentId)
            {
                otherSegmentsBefore[id!] = seg.GetProperty("translatedText").GetString();
            }
        }

        await coordinator.RegenerateSegmentTranslationAsync(segmentId!);

        var translationJsonAfter = await File.ReadAllTextAsync(coordinator.CurrentSession.TranslationPath);
        var dataAfter = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(translationJsonAfter);
        var segmentsAfter = dataAfter.GetProperty("segments");

        foreach (var seg in segmentsAfter.EnumerateArray())
        {
            var id = seg.GetProperty("id").GetString();
            if (id != segmentId && otherSegmentsBefore.ContainsKey(id))
            {
                var textAfter = seg.GetProperty("translatedText").GetString();
                Assert.Equal(otherSegmentsBefore[id], textAfter);
            }
        }
    }

    [Fact]
    public async Task Reopen_PreservesUpdatedTranslationSegment()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_regen_trans_reopen.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");

        var translationJsonBefore = await File.ReadAllTextAsync(coordinator.CurrentSession.TranslationPath);
        var dataBefore = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(translationJsonBefore);
        var segmentsBefore = dataBefore.GetProperty("segments");
        var segmentId = segmentsBefore[0].GetProperty("id").GetString();

        await coordinator.RegenerateSegmentTranslationAsync(segmentId!);

        var translationPath = coordinator.CurrentSession.TranslationPath;
        var sessionId = coordinator.CurrentSession.SessionId;

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Equal(sessionId, coordinator.CurrentSession.SessionId);
        Assert.NotNull(coordinator.CurrentSession.TranslationPath);

        var translationJsonAfter = await File.ReadAllTextAsync(coordinator.CurrentSession.TranslationPath);
        var dataAfter = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(translationJsonAfter);
        var segmentsAfter = dataAfter.GetProperty("segments");

        var foundUpdated = false;
        foreach (var seg in segmentsAfter.EnumerateArray())
        {
            var id = seg.GetProperty("id").GetString();
            if (id == segmentId)
            {
                foundUpdated = true;
                var translatedText = seg.GetProperty("translatedText").GetString();
                Assert.NotNull(translatedText);
                break;
            }
        }
        Assert.True(foundUpdated, "Updated segment should exist after reopen");
    }

    [Fact]
    public void BuildSegmentWorkflowList_ReflectsArtifactState()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_segment_list.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        coordinator.TranscribeMediaAsync().GetAwaiter().GetResult();
        coordinator.TranslateTranscriptAsync("en", "es").GetAwaiter().GetResult();
        coordinator.GenerateTtsAsync().GetAwaiter().GetResult();

        var segmentsList = coordinator.GetSegmentWorkflowList();

        Assert.NotEmpty(segmentsList);
        
        var firstSegment = segmentsList[0];
        Assert.NotNull(firstSegment.SegmentId);
        Assert.True(firstSegment.StartSeconds >= 0);
        Assert.True(firstSegment.EndSeconds > firstSegment.StartSeconds);
        Assert.False(string.IsNullOrEmpty(firstSegment.SourceText));
        Assert.True(firstSegment.HasTranslation);
        Assert.NotNull(firstSegment.TranslatedText);
    }

    [Fact]
    public void Reopen_ReconstructsMixedSegmentState()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_mixed_reopen.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        coordinator.TranscribeMediaAsync().GetAwaiter().GetResult();
        coordinator.TranslateTranscriptAsync("en", "es").GetAwaiter().GetResult();
        coordinator.GenerateTtsAsync().GetAwaiter().GetResult();

        var segmentsBefore = coordinator.GetSegmentWorkflowList();
        var sessionId = coordinator.CurrentSession.SessionId;

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        var segmentsAfter = coordinator.GetSegmentWorkflowList();

        Assert.Equal(sessionId, coordinator.CurrentSession.SessionId);
        Assert.Equal(segmentsBefore.Count, segmentsAfter.Count);

        for (int i = 0; i < segmentsBefore.Count; i++)
        {
            Assert.Equal(segmentsBefore[i].SegmentId, segmentsAfter[i].SegmentId);
            Assert.Equal(segmentsBefore[i].HasTranslation, segmentsAfter[i].HasTranslation);
            Assert.Equal(segmentsBefore[i].HasTtsAudio, segmentsAfter[i].HasTtsAudio);
        }
    }

    // --- Bug regression tests ---

    [Fact]
    public async Task RegenerateSegmentTranslation_ActuallyWritesNewTextToSegment()
    {
        // Bug 1: segmentId was never passed to the Python script, so the segment was never updated.
        // Strategy: corrupt a segment's translatedText to a sentinel, regenerate it, verify sentinel is gone.
        var stateFilePath = Path.Combine(_testStateDir, "session_regen_sentinel.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);
        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");

        var translationPath = coordinator.CurrentSession.TranslationPath!;

        // Get the first segment's ID
        var jsonBefore = await File.ReadAllTextAsync(translationPath);
        var dataBefore = JsonSerializer.Deserialize<JsonElement>(jsonBefore);
        var firstSeg = dataBefore.GetProperty("segments")[0];
        var segmentId = firstSeg.GetProperty("id").GetString()!;

        // Corrupt the segment's translatedText to a known sentinel
        const string sentinel = "CORRUPTED_SENTINEL_DO_NOT_PERSIST";
        var corrupted = JsonSerializer.Deserialize<JsonElement>(jsonBefore);
        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });
        RewriteJsonWithCorruptedSegment(corrupted, segmentId, sentinel, writer);
        writer.Flush();
        await File.WriteAllBytesAsync(translationPath, ms.ToArray());

        await coordinator.RegenerateSegmentTranslationAsync(segmentId);

        var jsonAfter = await File.ReadAllTextAsync(translationPath);
        var dataAfter = JsonSerializer.Deserialize<JsonElement>(jsonAfter);
        var segAfter = dataAfter.GetProperty("segments").EnumerateArray()
            .First(s => s.GetProperty("id").GetString() == segmentId);

        Assert.NotEqual(sentinel, segAfter.GetProperty("translatedText").GetString());
    }

    private static void RewriteJsonWithCorruptedSegment(
        JsonElement root, string targetId, string sentinel, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name != "segments")
            {
                prop.WriteTo(writer);
                continue;
            }
            writer.WritePropertyName("segments");
            writer.WriteStartArray();
            foreach (var seg in prop.Value.EnumerateArray())
            {
                var id = seg.GetProperty("id").GetString();
                if (id == targetId)
                {
                    writer.WriteStartObject();
                    foreach (var sp in seg.EnumerateObject())
                    {
                        if (sp.Name == "translatedText")
                            writer.WriteString("translatedText", sentinel);
                        else
                            sp.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    seg.WriteTo(writer);
                }
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    [Fact]
    public async Task TranslateTranscript_PersistsSourceLanguageToSnapshot()
    {
        // Bug 4: SourceLanguage was never stored in the snapshot, so RegenerateSegmentTranslationAsync
        // always used hardcoded "es" regardless of what language was originally used.
        var stateFilePath = Path.Combine(_testStateDir, "session_sourcelang.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);
        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");

        Assert.Equal("es", coordinator.CurrentSession.SourceLanguage);
        Assert.Equal("en", coordinator.CurrentSession.TargetLanguage);
    }

    [Fact]
    public async Task TranslateTranscript_ThenReopen_PreservesSourceLanguage()
    {
        // Bug 4 regression: SourceLanguage must survive a session reopen.
        var stateFilePath = Path.Combine(_testStateDir, "session_sourcelang_reopen.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);
        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");

        coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        Assert.Equal("es", coordinator.CurrentSession.SourceLanguage);
    }

    [Fact]
    public async Task GenerateTts_SetsSegmentTrackingStructures()
    {
        // Bug 3: GenerateTtsAsync set CurrentSession twice; first write was discarded.
        // Verify the final snapshot has TtsSegmentsPath and an empty TtsSegmentAudioPaths dict.
        var stateFilePath = Path.Combine(_testStateDir, "session_tts_tracking.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);
        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");
        await coordinator.GenerateTtsAsync();

        Assert.NotNull(coordinator.CurrentSession.TtsSegmentsPath);
        Assert.NotNull(coordinator.CurrentSession.TtsSegmentAudioPaths);
        Assert.Empty(coordinator.CurrentSession.TtsSegmentAudioPaths);
    }

    [Fact]
    public async Task RegenerateSegmentTranslation_ThrowsOnFailedTranslation()
    {
        // Bug 2: result.Success was never checked, so a failed translation was treated as success.
        // Use a nonexistent segment ID to trigger the "segment not found" error path.
        var stateFilePath = Path.Combine(_testStateDir, "session_regen_fail.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);
        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync("en", "es");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.RegenerateSegmentTranslationAsync("segment_nonexistent"));
    }

    [Fact]
    public void RegeneratedAndUntouchedSegments_RemainDistinct()
    {
        var stateFilePath = Path.Combine(_testStateDir, "session_distinct.json");
        _lastStateFilePath = stateFilePath;

        var log = new AppLog(GetTestLogPath());
        var store = new SessionSnapshotStore(stateFilePath, log);

        var coordinator = new SessionWorkflowCoordinator(store, log);
        coordinator.Initialize();

        coordinator.LoadMedia(_testMediaPath);
        coordinator.TranscribeMediaAsync().GetAwaiter().GetResult();
        coordinator.TranslateTranscriptAsync("en", "es").GetAwaiter().GetResult();
        coordinator.GenerateTtsAsync().GetAwaiter().GetResult();

        var segmentsBefore = coordinator.GetSegmentWorkflowList();
        Assert.True(segmentsBefore.Count >= 2, "Need at least 2 segments");

        var firstSegmentId = segmentsBefore[0].SegmentId;
        var secondSegmentId = segmentsBefore[1].SegmentId;

        coordinator.RegenerateSegmentTtsAsync(firstSegmentId).GetAwaiter().GetResult();

        var segmentsAfter = coordinator.GetSegmentWorkflowList();

        var firstAfter = segmentsAfter[0];
        var secondAfter = segmentsAfter[1];

        Assert.Equal(firstSegmentId, firstAfter.SegmentId);
        Assert.Equal(secondSegmentId, secondAfter.SegmentId);
        
        Assert.True(firstAfter.HasTtsAudio);
        Assert.False(secondAfter.HasTtsAudio);
    }
}
