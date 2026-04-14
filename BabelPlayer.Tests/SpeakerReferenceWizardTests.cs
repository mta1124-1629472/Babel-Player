using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public void BuildPersistencePayload_OnlyIncludesChangedItems()
    {
        var unchanged = new SpeakerReferenceDraftItem("spk_00", @"C:\same.wav");
        var changed = new SpeakerReferenceDraftItem("spk_01", @"C:\old.wav");
        changed.SetDraftReferencePath(@"C:\new.wav", "Browse");
        var cleared = new SpeakerReferenceDraftItem("spk_02", @"C:\old2.wav");
        cleared.SetDraftReferencePath(string.Empty, "Clear");

        var payload = SpeakerReferenceWizardViewModel.BuildPersistencePayload([unchanged, changed, cleared]);

        Assert.Equal(2, payload.Count);
        Assert.Equal(@"C:\new.wav", payload["spk_01"]);
        Assert.Null(payload["spk_02"]);
        Assert.False(payload.ContainsKey("spk_00"));
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
