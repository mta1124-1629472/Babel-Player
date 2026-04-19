using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;

namespace BabelPlayer.Tests;

public sealed class SpeakerReferenceWizardTests
{
    [Fact]
    public void EvaluateMetrics_HealthyClip_ReturnsGoodTier()
    {
        var metrics = new SpeakerReferenceClipQualityMetrics(
            DurationSeconds: 9.5,
            MeanVolumeDb: -20.0,
            MaxVolumeDb: -3.2,
            NonSilentRatio: 0.92);

        var result = SpeakerReferenceClipQualityEvaluator.EvaluateMetrics(metrics);

        Assert.Equal(SpeakerReferenceConfidenceTier.Good, result.Tier);
        Assert.Contains("look good", result.Reasons.Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateMetrics_ShortSilentClip_ReturnsPoorTierWithReasons()
    {
        var metrics = new SpeakerReferenceClipQualityMetrics(
            DurationSeconds: 2.0,
            MeanVolumeDb: -42.0,
            MaxVolumeDb: -0.2,
            NonSilentRatio: 0.30);

        var result = SpeakerReferenceClipQualityEvaluator.EvaluateMetrics(metrics);

        Assert.Equal(SpeakerReferenceConfidenceTier.Poor, result.Tier);
        Assert.Contains(result.Reasons, reason => reason.Contains("shorter", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Reasons, reason => reason.Contains("silence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DraftItem_IsChangedTracksMutations()
    {
        var item = new SpeakerReferenceDraftItem("spk_00", @"C:\clip-a.wav");
        Assert.False(item.IsChanged);

        item.SetDraftReferencePath(@"C:\clip-b.wav", "Browse");
        Assert.True(item.IsChanged);

        item.RestoreAuto();
        Assert.False(item.IsChanged);
    }

    [Fact]
    public void BuildReferencePersistencePayload_OnlyIncludesChangedItems()
    {
        var unchanged = new SpeakerReferenceDraftItem("spk_00", @"C:\same.wav");
        var changed = new SpeakerReferenceDraftItem("spk_01", @"C:\old.wav");
        changed.SetDraftReferencePath(@"C:\new.wav", "Browse");
        var cleared = new SpeakerReferenceDraftItem("spk_02", @"C:\old2.wav");
        cleared.SetDraftReferencePath(string.Empty, "Clear");

        var payload = SpeakerReferenceWizardViewModel.BuildReferencePersistencePayload([unchanged, changed, cleared]);

        Assert.Equal(2, payload.Count);
        Assert.Equal(@"C:\new.wav", payload["spk_01"]);
        Assert.Null(payload["spk_02"]);
        Assert.False(payload.ContainsKey("spk_00"));
    }

    [Fact]
    public void BuildVoicePersistencePayload_OnlyIncludesChangedVoices()
    {
        var a = new SpeakerReferenceDraftItem("spk_00", @"C:\x.wav", "voice-a");
        var b = new SpeakerReferenceDraftItem("spk_01", @"C:\y.wav", "voice-b");
        b.SetDraftVoice("voice-c", "Edit");

        var payload = SpeakerReferenceWizardViewModel.BuildVoicePersistencePayload([a, b]);

        Assert.Single(payload);
        Assert.Equal("voice-c", payload["spk_01"]);
    }

    [Fact]
    public void DraftItem_VoiceChangeSetsIsChanged()
    {
        var item = new SpeakerReferenceDraftItem("spk_00", @"C:\a.wav", "v1");
        Assert.False(item.IsChanged);
        item.SetDraftVoice("v2", "Edit");
        Assert.True(item.IsVoiceChanged);
        Assert.True(item.IsChanged);
    }

    [Fact]
    public void ApplySpeakerVoiceAssignmentChanges_AppliesOnlyProvidedDiffs()
    {
        using var harness = new CoordinatorHarness();
        var coordinator = harness.CreateCoordinator();
        coordinator.Initialize();
        coordinator.SetSpeakerVoiceAssignment("spk_00", "a");
        coordinator.SetSpeakerVoiceAssignment("spk_01", "b");

        coordinator.ApplySpeakerVoiceAssignmentChanges(new Dictionary<string, string?>
        {
            ["spk_00"] = "a",
            ["spk_01"] = "c",
            ["spk_02"] = null,
        });

        var voices = coordinator.GetSpeakerVoiceAssignments();
        Assert.Equal("a", voices["spk_00"]);
        Assert.Equal("c", voices["spk_01"]);
        Assert.False(voices.ContainsKey("spk_02"));
    }

    [Fact]
    public void FilterDraftItems_ShowLowConfidenceOnly_FiltersGoodItems()
    {
        var good = new SpeakerReferenceDraftItem("spk_00", @"C:\good.wav")
        {
            ConfidenceTier = SpeakerReferenceConfidenceTier.Good,
        };
        var poor = new SpeakerReferenceDraftItem("spk_01", @"C:\poor.wav")
        {
            ConfidenceTier = SpeakerReferenceConfidenceTier.Poor,
        };

        var filtered = SpeakerReferenceWizardViewModel.FilterDraftItems([good, poor], showLowConfidenceOnly: true);

        Assert.Single(filtered);
        Assert.Equal("spk_01", filtered[0].SpeakerId);
    }

    [Fact]
    public void ComputeClipStartAndBounds_ClampsWindowAndEndOfMedia()
    {
        var (start, _) = SpeakerWizardPlayheadHelper.ComputeClipStartAndBounds(
            centerSec: 100.0,
            windowSec: 8.0,
            mediaDurationSec: 60.0);

        Assert.Equal(52.0, start, precision: 5);

        var (start2, _) = SpeakerWizardPlayheadHelper.ComputeClipStartAndBounds(
            centerSec: 5.0,
            windowSec: 10.0,
            mediaDurationSec: 120.0);

        Assert.Equal(0.0, start2, precision: 5);
    }

    [Fact]
    public void ListDownloadedPiperVoiceIds_ReturnsVoicesWithOnnxAndSidecarJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"babel-piper-list-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "en_US-test.onnx"), "x");
            File.WriteAllText(Path.Combine(dir, "en_US-test.onnx.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "orphan.onnx"), "x");

            var list = ModelDownloader.ListDownloadedPiperVoiceIds(dir);

            Assert.Single(list);
            Assert.Equal("en_US-test", list[0]);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public async Task MergeDiarizedSpeakersAsync_RewritesArtifactsAndRemapsSessionMaps()
    {
        using var harness = new CoordinatorHarness();
        var coordinator = harness.CreateCoordinator();
        coordinator.Initialize();

        var dir = Path.Combine(Path.GetTempPath(), $"babel-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var transcriptPath = Path.Combine(dir, "transcript.json");
        await File.WriteAllTextAsync(
            transcriptPath,
            """
            {
              "schema_version": "2.0",
              "language": "en",
              "language_probability": 1,
              "segments": [
                { "start": 0, "end": 1, "text": "a", "speakerId": "spk_a" },
                { "start": 1, "end": 2, "text": "b", "speakerId": "spk_b" }
              ]
            }
            """);

        var translationPath = Path.Combine(dir, "translation.json");
        await File.WriteAllTextAsync(
            translationPath,
            """
            {
              "schema_version": "2.0",
              "sourceLanguage": "en",
              "targetLanguage": "de",
              "segments": [
                { "id": "s1", "start": 0, "end": 1, "text": "a", "translatedText": "a", "speakerId": "spk_a" },
                { "id": "s2", "start": 1, "end": 2, "text": "b", "translatedText": "b", "speakerId": "spk_b" }
              ]
            }
            """);

        coordinator.CurrentSession = coordinator.CurrentSession with
        {
            Stage = SessionWorkflowStage.Translated,
            TranscriptPath = transcriptPath,
            TranslationPath = translationPath,
            SpeakerVoiceAssignments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["spk_a"] = "va",
                ["spk_b"] = "vb",
            },
            SpeakerReferenceAudioPaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["spk_a"] = @"C:\ra.wav",
                ["spk_b"] = @"C:\rb.wav",
            },
        };

        var changed = await coordinator.MergeDiarizedSpeakersAsync("spk_b", "spk_a");

        Assert.Equal(1, changed);

        var transcript = await ArtifactJson.LoadTranscriptAsync(transcriptPath);
        Assert.NotNull(transcript.Segments);
        Assert.All(transcript.Segments, s => Assert.Equal("spk_a", s.SpeakerId));

        var translation = await ArtifactJson.LoadTranslationAsync(translationPath);
        Assert.NotNull(translation.Segments);
        Assert.All(translation.Segments, s => Assert.Equal("spk_a", s.SpeakerId));

        var voices = coordinator.GetSpeakerVoiceAssignments();
        Assert.Equal("va", voices["spk_a"]);
        Assert.False(voices.ContainsKey("spk_b"));

        var refs = coordinator.GetSpeakerReferenceAudioPaths();
        Assert.Equal(@"C:\ra.wav", refs["spk_a"]);
        Assert.False(refs.ContainsKey("spk_b"));

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void ApplySpeakerReferenceAudioPathChanges_AppliesOnlyProvidedDiffs()
    {
        using var harness = new CoordinatorHarness();
        var coordinator = harness.CreateCoordinator();
        coordinator.Initialize();
        coordinator.SetSpeakerReferenceAudioPath("spk_00", @"C:\a.wav");
        coordinator.SetSpeakerReferenceAudioPath("spk_01", @"C:\b.wav");

        coordinator.ApplySpeakerReferenceAudioPathChanges(new Dictionary<string, string?>
        {
            ["spk_00"] = @"C:\a.wav", // unchanged
            ["spk_01"] = @"C:\c.wav", // update
            ["spk_02"] = null,         // no-op remove
        });

        var refs = coordinator.GetSpeakerReferenceAudioPaths();
        Assert.Equal(@"C:\a.wav", refs["spk_00"]);
        Assert.Equal(@"C:\c.wav", refs["spk_01"]);
        Assert.False(refs.ContainsKey("spk_02"));
    }

    private sealed class CoordinatorHarness : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-ref-wizard-tests-{Guid.NewGuid():N}");
        private readonly AppLog _log;
        private readonly SessionSnapshotStore _store;
        private readonly PerSessionSnapshotStore _perSessionStore;
        private readonly RecentSessionsStore _recentStore;
        private readonly AppSettings _settings = new();

        public CoordinatorHarness()
        {
            Directory.CreateDirectory(_dir);
            _log = new AppLog(Path.Combine(_dir, "test.log"));
            _store = new SessionSnapshotStore(Path.Combine(_dir, "session.json"), _log);
            _perSessionStore = new PerSessionSnapshotStore(Path.Combine(_dir, "sessions"), _log);
            _recentStore = new RecentSessionsStore(Path.Combine(_dir, "recent-sessions.json"), _log);
        }

        public SessionWorkflowCoordinator CreateCoordinator()
        {
            var registries = new RegistryBundle(
                _perSessionStore,
                _recentStore,
                new TranscriptionRegistry(_log),
                new TranslationRegistry(_log),
                new TtsRegistry(_log));

            var coreServices = new CoordinatorCoreServices(_store, _log, _settings);
            return new SessionWorkflowCoordinator(coreServices, registries);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}
